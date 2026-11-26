using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace KageDesk.App.Native;

/// <summary>
/// Authenticated, current-user-scoped Named Pipe IPC for Single-Instance Wake-Up UX.
/// Restricts pipe ACL strictly to the current user's SID (PipeAccessRights.ReadWrite),
/// validates a strict 8-byte wake opcode, and coalesces/rate-limits receiver callbacks.
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
    /// Returns true only if the 8-byte wake opcode was delivered and acknowledged.
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

        while (true)
        {
            long remainingMs = Math.Max(1, deadlineTick - Environment.TickCount64);
            int connectTimeoutMs = (int)Math.Min(250, remainingMs);

            try
            {
                using var client = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: targetPipe,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.CurrentUserOnly,
                    impersonationLevel: TokenImpersonationLevel.None);

                client.Connect(connectTimeoutMs);
                client.Write(WakeOpcodeBytes, 0, WakeOpcodeBytes.Length);
                client.Flush();

                int ack = client.ReadByte();
                if (ack == AckByte)
                {
                    return true;
                }
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

            if (Environment.TickCount64 >= deadlineTick)
            {
                return false;
            }

            Thread.Sleep(50);
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
    private readonly object _rateLock = new();

    private Task? _listenTask;
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
        if (_disposed || _started) return;
        _started = true;

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

        _listenTask = Task.Factory.StartNew(
            () => ListenLoopAsync(initialServer, _cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    private NamedPipeServerStream CreateServerInstance()
    {
        PipeSecurity security = SingleInstanceWakeUp.CreateCurrentUserPipeSecurity();
        return NamedPipeServerStreamAcl.Create(
            pipeName: _pipeName,
            direction: PipeDirection.InOut,
            maxNumberOfServerInstances: 2,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous,
            inBufferSize: 64,
            outBufferSize: 64,
            pipeSecurity: security);
    }

    private async Task ListenLoopAsync(NamedPipeServerStream? initialServer, CancellationToken ct)
    {
        NamedPipeServerStream? pendingServer = initialServer;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                if (pendingServer != null)
                {
                    server = pendingServer;
                    pendingServer = null;
                }
                else
                {
                    server = CreateServerInstance();
                }

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

        if (pendingServer != null)
        {
            try { pendingServer.Dispose(); } catch { }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromMilliseconds(500));

        byte[] buf = new byte[SingleInstanceWakeUp.OpcodeLength];
        int total = 0;
        while (total < buf.Length)
        {
            int n = await server.ReadAsync(buf, total, buf.Length - total, readCts.Token).ConfigureAwait(false);
            if (n <= 0) return;
            total += n;
        }

        if (!buf.AsSpan().SequenceEqual(SingleInstanceWakeUp.WakeOpcode))
        {
            // Invalid opcode: reject without waking UI
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try
        {
            _listenTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch { }
        _cts.Dispose();
    }
}
