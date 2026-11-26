using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KageDesk.App.Native;

/// <summary>
/// Authenticated, current-user-scoped Named Pipe IPC for Single-Instance Wake-Up UX.
/// Restricts pipe ACL strictly to the current user's SID (PipeAccessRights.FullControl),
/// enforces exact 8-byte wake opcode framing without trailing bytes before ACK,
/// bounds all client connect/write/flush/read I/O within a single timeout budget,
/// and coalesces/rate-limits receiver callbacks.
/// </summary>
public static class SingleInstanceWakeUp
{
    public const string DispatcherWindowTitle = "KageDesk.Dispatcher.v1";
    public const int OpcodeLength = 8;
    public static readonly TimeSpan DefaultClientRetryWindow = TimeSpan.FromMilliseconds(2500);
    public static readonly TimeSpan DefaultRateLimitWindow = TimeSpan.FromSeconds(2);

    private static readonly byte[] WakeOpcodeBytes = "KDWK0001"u8.ToArray();
    private const byte AckByte = 0x01;

    private static readonly Lazy<string> DefaultPipeNameLazy = new(ComputeDefaultPipeName);

    public static string DefaultPipeName => DefaultPipeNameLazy.Value;

    public static ReadOnlySpan<byte> WakeOpcode => WakeOpcodeBytes;

    private static string ComputeDefaultPipeName()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"KageDesk.WakeUp.v2:{sid}"));
        return $"KageDesk.WakeUp.v2.{Convert.ToHexString(hash)[..24]}";
    }

    public static PipeSecurity CreateCurrentUserPipeSecurity()
    {
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve current Windows user SID for Named Pipe ACL.");

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Connects to the running instance's current-user Named Pipe server with bounded retry
    /// (up to 2.5 s by default) to tolerate startup races while the first instance initializes.
    /// All connect/write/flush/read operations share a single per-attempt timeout bounded by
    /// the remaining retry budget so a stalled server cannot hang the caller indefinitely.
    /// Returns true only if the exact 8-byte wake opcode was delivered and acknowledged.
    /// </summary>
    public static bool TrySignalRunningInstance(
        string? pipeName = null,
        TimeSpan? maxRetryWindow = null)
    {
        string targetPipe = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;
        TimeSpan budget = maxRetryWindow ?? DefaultClientRetryWindow;
        if (budget < TimeSpan.Zero) budget = TimeSpan.Zero;

        long startTick = Environment.TickCount64;
        long deadlineTick = startTick + (long)budget.TotalMilliseconds;
        byte[] ackBuf = new byte[1];

        while (true)
        {
            long remainingMs = Math.Max(1, deadlineTick - Environment.TickCount64);
            int attemptTimeoutMs = (int)Math.Min(500, remainingMs);

            try
            {
                using var attemptCts = new CancellationTokenSource(attemptTimeoutMs);
                using var client = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: targetPipe,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous,
                    impersonationLevel: TokenImpersonationLevel.None);

                client.ConnectAsync(attemptCts.Token).GetAwaiter().GetResult();
                client.WriteAsync(WakeOpcodeBytes, 0, WakeOpcodeBytes.Length, attemptCts.Token).GetAwaiter().GetResult();
                client.FlushAsync(attemptCts.Token).GetAwaiter().GetResult();

                int read = client.ReadAsync(ackBuf, 0, 1, attemptCts.Token).GetAwaiter().GetResult();
                if (read == 1 && ackBuf[0] == AckByte)
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                // Attempt timed out (server starting or stalled); retry if budget remains
            }
            catch (TimeoutException)
            {
                // Server may still be starting; retry within budget
            }
            catch (IOException)
            {
                // Transient pipe reset/disconnect; retry within budget
            }
            catch (UnauthorizedAccessException)
            {
                // Denied by ACL — do not spin
                return false;
            }
            catch
            {
                // Retry within budget
            }

            long leftMs = deadlineTick - Environment.TickCount64;
            if (leftMs <= 0)
            {
                return false;
            }

            Thread.Sleep((int)Math.Min(50, leftMs));
        }
    }
}

public sealed class SingleInstanceWakeUpServer : IDisposable
{
    private static readonly byte[] AckBytes = { 0x01 };

    private readonly string _pipeName;
    private readonly Action _onWakeUp;
    private readonly TimeSpan _rateLimitWindow;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lifecycleLock = new();
    private readonly object _rateLock = new();

    private Task? _listenTask;
    private NamedPipeServerStream? _pendingServer;
    private DateTimeOffset _lastAcceptedWakeAt = DateTimeOffset.MinValue;
    private bool _started;
    private bool _disposed;

    public string PipeName => _pipeName;
    internal Exception? LastListenException { get; private set; }

    public SingleInstanceWakeUpServer(
        Action onWakeUp,
        string? pipeName = null,
        TimeSpan? rateLimitWindow = null,
        Func<DateTimeOffset>? clock = null)
    {
        _onWakeUp = onWakeUp ?? throw new ArgumentNullException(nameof(onWakeUp));
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? SingleInstanceWakeUp.DefaultPipeName : pipeName;
        _rateLimitWindow = rateLimitWindow ?? SingleInstanceWakeUp.DefaultRateLimitWindow;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _started) return;
            _started = true;

            // Capture token before scheduling so Dispose() cannot cause ObjectDisposedException
            // on _cts.Token before ListenLoopAsync enters its try/finally block.
            CancellationToken ct = _cts.Token;

            // Pre-create the first pipe instance synchronously so the kernel pipe endpoint
            // exists immediately before Start() returns (zero ThreadPool startup race).
            NamedPipeServerStream? initialServer = null;
            try
            {
                initialServer = CreateServerInstance();
            }
            catch (Exception ex)
            {
                LastListenException = ex;
            }

            _pendingServer = initialServer;

            try
            {
                // Pass CancellationToken.None so the listener body always runs its finally block.
                // PipeOptions.Asynchronous uses IOCP so Task.Run does not block a thread or pay OS thread creation cost.
                _listenTask = Task.Run(() => ListenLoopAsync(ct), CancellationToken.None);
            }
            catch (Exception ex)
            {
                LastListenException = ex;
                NamedPipeServerStream? leftover = Interlocked.Exchange(ref _pendingServer, null);
                if (leftover != null)
                {
                    try { leftover.Dispose(); } catch { }
                }
            }
        }
    }

    private NamedPipeServerStream CreateServerInstance()
    {
        PipeSecurity security = SingleInstanceWakeUp.CreateCurrentUserPipeSecurity();
        return NamedPipeServerStreamAcl.Create(
            pipeName: _pipeName,
            direction: PipeDirection.InOut,
            maxNumberOfServerInstances: 2,
            transmissionMode: PipeTransmissionMode.Message,
            options: PipeOptions.Asynchronous,
            inBufferSize: 64,
            outBufferSize: 64,
            pipeSecurity: security);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = Interlocked.Exchange(ref _pendingServer, null) ?? CreateServerInstance();

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    await HandleClientAsync(server, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LastListenException = ex;
                    if (ct.IsCancellationRequested) break;
                    try { await Task.Delay(50, ct).ConfigureAwait(false); } catch { break; }
                }
                finally
                {
                    if (server != null)
                    {
                        try { server.Dispose(); } catch { }
                    }
                }
            }
        }
        finally
        {
            NamedPipeServerStream? leftover = Interlocked.Exchange(ref _pendingServer, null);
            if (leftover != null)
            {
                try { leftover.Dispose(); } catch { }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromMilliseconds(500));

        // Read into an OpcodeLength + 1 buffer in message mode so any oversized message or
        // trailing bytes are detected and rejected BEFORE sending ACK.
        byte[] buf = new byte[SingleInstanceWakeUp.OpcodeLength + 1];
        int total = 0;
        bool messageComplete = false;
        while (total <= SingleInstanceWakeUp.OpcodeLength)
        {
            int n = await server.ReadAsync(buf, total, buf.Length - total, readCts.Token).ConfigureAwait(false);
            if (n <= 0) return;
            total += n;
            if (server.IsMessageComplete)
            {
                messageComplete = true;
                break;
            }
        }

        if (!messageComplete || total != SingleInstanceWakeUp.OpcodeLength)
        {
            // Fragmented, truncated, or trailing bytes in wake message: reject BEFORE ACK.
            return;
        }

        if (!buf.AsSpan(0, SingleInstanceWakeUp.OpcodeLength).SequenceEqual(SingleInstanceWakeUp.WakeOpcode))
        {
            // Invalid opcode: reject without waking UI
            return;
        }

        // Also reject if any extra trailing bytes/messages are already buffered on the pipe BEFORE sending ACK.
        if (HasPendingPipeBytes(server.SafePipeHandle))
        {
            return;
        }

        // Acknowledge valid opcode asynchronously so PipeOptions.Asynchronous never faults,
        // and allow the client to read the ACK byte before closing the pipe instance.
        try
        {
            await server.WriteAsync(AckBytes, readCts.Token).ConfigureAwait(false);
            await server.FlushAsync(readCts.Token).ConfigureAwait(false);

            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drainCts.CancelAfter(TimeSpan.FromMilliseconds(100));
            byte[] eofProbe = new byte[1];
            try
            {
                await server.ReadAsync(eofProbe, drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
        catch { }

        // Coalesce & rate-limit wake-up UI triggers (default: max 1 per 2s)
        bool shouldDispatch = false;
        lock (_rateLock)
        {
            DateTimeOffset now = _clock();
            if (_lastAcceptedWakeAt == DateTimeOffset.MinValue || (now - _lastAcceptedWakeAt) >= _rateLimitWindow)
            {
                _lastAcceptedWakeAt = now;
                shouldDispatch = true;
            }
        }

        if (shouldDispatch && !ct.IsCancellationRequested)
        {
            try
            {
                _onWakeUp();
            }
            catch { }
        }
    }

    private static bool HasPendingPipeBytes(SafePipeHandle pipeHandle)
    {
        if (pipeHandle.IsInvalid || pipeHandle.IsClosed)
        {
            return true;
        }

        if (!PeekNamedPipe(pipeHandle, IntPtr.Zero, 0, IntPtr.Zero, out uint totalBytesAvail, IntPtr.Zero))
        {
            return true;
        }

        return totalBytesAvail > 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(
        SafePipeHandle hNamedPipe,
        IntPtr lpBuffer,
        uint nBufferSize,
        IntPtr lpBytesRead,
        out uint lpTotalBytesAvail,
        IntPtr lpBytesLeftThisMessage);

    public void Dispose()
    {
        Task? listenTask;
        NamedPipeServerStream? unclaimed;
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            unclaimed = Interlocked.Exchange(ref _pendingServer, null);
            listenTask = _listenTask;
        }

        if (unclaimed != null)
        {
            try { unclaimed.Dispose(); } catch { }
        }

        bool completed = true;
        try
        {
            if (listenTask != null)
            {
                completed = listenTask.Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch { }

        if (completed)
        {
            _cts.Dispose();
        }
    }
}
