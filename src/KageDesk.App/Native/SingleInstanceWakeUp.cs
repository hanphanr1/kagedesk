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
/// Avoids PipeOptions.CurrentUserOnly (which compares default token owner SID rather than
/// User SID on .NET 8 and breaks same-user elevated &lt;-&gt; unelevated wakes per dotnet/runtime#123903).
/// Instead enforces:
/// - Protected per-user DACL granting PipeAccessRights.ReadWrite ONLY to current user's User SID
/// - Explicit server pipe Owner SID set to current user's User SID (even when server runs elevated)
/// - Client-side server pipe Owner SID verification before sending wake opcode
/// - Server-side connected peer User SID verification via RunAsClient before sending ACK or waking UI
/// - Exact 8-byte wake opcode framing without trailing bytes, bounded client I/O timeouts, and rate-limiting.
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

    internal readonly record struct PeerTokenIdentity(
        SecurityIdentifier? UserSid,
        SecurityIdentifier? DefaultOwnerSid
    );

    private static string ComputeDefaultPipeName()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"KageDesk.WakeUp.v2:{sid}"));
        return $"KageDesk.WakeUp.v2.{Convert.ToHexString(hash)[..24]}";
    }

    internal static bool IsAuthorizedSameUserPeer(
        SecurityIdentifier? expectedUserSid,
        SecurityIdentifier? actualPeerUserSid)
    {
        return expectedUserSid != null &&
               actualPeerUserSid != null &&
               expectedUserSid.Equals(actualPeerUserSid);
    }

    internal static bool IsAuthorizedCrossElevationPeer(
        PeerTokenIdentity localToken,
        PeerTokenIdentity peerToken)
    {
        // Deliberately compares User SID (NOT DefaultOwnerSid, which is BUILTIN\Administrators
        // for elevated UAC tokens and the user's own SID for standard tokens).
        return IsAuthorizedSameUserPeer(localToken.UserSid, peerToken.UserSid);
    }

    public static PipeSecurity CreateCurrentUserPipeSecurity() => CreateCurrentUserPipeSecurity(userSid: null);

    internal static PipeSecurity CreateCurrentUserPipeSecurity(SecurityIdentifier? userSid)
    {
        SecurityIdentifier currentUser = userSid
            ?? WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve current Windows user SID for Named Pipe ACL.");

        var security = new PipeSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Connects to the running instance's current-user Named Pipe server with bounded retry
    /// (up to 2.5 s by default) to tolerate startup races while the first instance initializes.
    /// Verifies the server pipe's Owner SID matches the current user's User SID before writing,
    /// allowing same-user elevated &lt;-&gt; unelevated wakeups while rejecting other users.
    /// Returns true only if the exact 8-byte wake opcode was delivered and acknowledged.
    /// </summary>
    public static bool TrySignalRunningInstance(
        string? pipeName = null,
        TimeSpan? maxRetryWindow = null)
    {
        return TrySignalRunningInstanceCore(
            pipeName,
            maxRetryWindow,
            expectedUserSid: null,
            resolveServerOwnerSid: null);
    }

    internal static bool TrySignalRunningInstanceCore(
        string? pipeName,
        TimeSpan? maxRetryWindow,
        SecurityIdentifier? expectedUserSid,
        Func<NamedPipeClientStream, SecurityIdentifier?>? resolveServerOwnerSid,
        Action<string>? onAttemptFailure = null)
    {
        SecurityIdentifier? localUserSid = expectedUserSid ?? WindowsIdentity.GetCurrent().User;
        if (localUserSid == null)
        {
            onAttemptFailure?.Invoke("localUserSid=null");
            return false;
        }

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
            string stage = "init";

            try
            {
                // Do NOT set PipeOptions.CurrentUserOnly (dotnet/runtime#123903 breaks same-user
                // elevated <-> unelevated pipes). Use PipeOptions.None + CancelIoEx so normal
                // handshakes execute directly via kernel Named Pipe I/O with zero ThreadPool
                // dependency, while TokenImpersonationLevel.Identification lets the server verify
                // our exact User SID via RunAsClient.
                using var client = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: targetPipe,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.None,
                    impersonationLevel: TokenImpersonationLevel.Identification);

                stage = "connect";
                client.Connect(attemptTimeoutMs);

                // Verify server pipe Owner SID == current user's User SID BEFORE writing opcode
                stage = "owner";
                SecurityIdentifier? serverOwnerSid = resolveServerOwnerSid != null
                    ? resolveServerOwnerSid(client)
                    : GetServerPipeOwnerSid(client);
                if (!IsAuthorizedSameUserPeer(localUserSid, serverOwnerSid))
                {
                    onAttemptFailure?.Invoke($"t={Environment.TickCount64 - startTick}ms owner-mismatch local={localUserSid} server={serverOwnerSid}");
                    return false;
                }

                int ioTimeoutMs = Math.Max(100, (int)Math.Min(500, budget.TotalMilliseconds));
                using var ioCts = new CancellationTokenSource(ioTimeoutMs);
                using var ioCancelReg = ioCts.Token.Register(() => TryCancelPipeIo(client));

                stage = "write";
                client.Write(WakeOpcodeBytes, 0, WakeOpcodeBytes.Length);
                stage = "flush";
                client.Flush();

                stage = "read";
                int read = client.Read(ackBuf, 0, 1);
                if (read == 1 && ackBuf[0] == AckByte)
                {
                    return true;
                }
                onAttemptFailure?.Invoke($"t={Environment.TickCount64 - startTick}ms bad-ack read={read} byte={ackBuf[0]}");
            }
            catch (OperationCanceledException ex)
            {
                onAttemptFailure?.Invoke($"t={Environment.TickCount64 - startTick}ms @{stage} OCE: {ex.Message}");
            }
            catch (TimeoutException ex)
            {
                onAttemptFailure?.Invoke($"t={Environment.TickCount64 - startTick}ms @{stage} Timeout: {ex.Message}");
            }
            catch (IOException ex)
            {
                onAttemptFailure?.Invoke($"t={Environment.TickCount64 - startTick}ms @{stage} IOE: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                onAttemptFailure?.Invoke($"t={Environment.TickCount64 - startTick}ms @{stage} UAE: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                onAttemptFailure?.Invoke($"t={Environment.TickCount64 - startTick}ms @{stage} {ex.GetType().Name}: {ex.Message}");
            }

            long leftMs = deadlineTick - Environment.TickCount64;
            if (leftMs <= 0)
            {
                return false;
            }

            Thread.Sleep((int)Math.Min(50, leftMs));
        }
    }

    internal static void TryCancelPipeIo(PipeStream? stream)
    {
        if (stream == null) return;
        bool addedRef = false;
        SafePipeHandle? handle = null;
        try
        {
            handle = stream.SafePipeHandle;
            if (handle.IsInvalid || handle.IsClosed) return;
            handle.DangerousAddRef(ref addedRef);
            if (!handle.IsInvalid && !handle.IsClosed)
            {
                CancelIoEx(handle, IntPtr.Zero);
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        finally
        {
            if (addedRef && handle != null)
            {
                handle.DangerousRelease();
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafePipeHandle hFile, IntPtr lpOverlapped);

    private static SecurityIdentifier? GetServerPipeOwnerSid(NamedPipeClientStream client)
    {
        PipeSecurity security = PipesAclExtensions.GetAccessControl(client);
        return security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
    }
}

public sealed class SingleInstanceWakeUpServer : IDisposable
{
    private static readonly byte[] AckBytes = { 0x01 };

    private readonly string _pipeName;
    private readonly Action _onWakeUp;
    private readonly TimeSpan _rateLimitWindow;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SecurityIdentifier _expectedUserSid;
    private readonly Func<NamedPipeServerStream, SecurityIdentifier?>? _resolvePeerUserSidOverride;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lifecycleLock = new();
    private readonly object _rateLock = new();

    private Thread? _listenThread;
    private NamedPipeServerStream? _pendingServer;
    private NamedPipeServerStream? _activeServer;
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
        : this(onWakeUp, pipeName, rateLimitWindow, clock, expectedUserSid: null, resolvePeerUserSidOverride: null)
    {
    }

    internal SingleInstanceWakeUpServer(
        Action onWakeUp,
        string? pipeName,
        TimeSpan? rateLimitWindow,
        Func<DateTimeOffset>? clock,
        SecurityIdentifier? expectedUserSid,
        Func<NamedPipeServerStream, SecurityIdentifier?>? resolvePeerUserSidOverride)
    {
        _onWakeUp = onWakeUp ?? throw new ArgumentNullException(nameof(onWakeUp));
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? SingleInstanceWakeUp.DefaultPipeName : pipeName;
        _rateLimitWindow = rateLimitWindow ?? SingleInstanceWakeUp.DefaultRateLimitWindow;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _expectedUserSid = expectedUserSid
            ?? WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve current Windows user SID for Named Pipe server.");
        _resolvePeerUserSidOverride = resolvePeerUserSidOverride;
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _started) return;
            _started = true;

            // Capture token before starting listener thread so Dispose() cannot race on _cts.Token.
            CancellationToken ct = _cts.Token;

            // Pre-create the first pipe instance synchronously so the kernel pipe endpoint
            // exists immediately before Start() returns (zero startup race).
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
                // Run the Named Pipe server on a dedicated background thread using synchronous
                // PipeOptions.None I/O + CancelIoEx so wake-up handshakes complete directly in
                // kernel I/O with zero .NET ThreadPool dependency even under heavy load.
                var thread = new Thread(() => ListenLoop(ct))
                {
                    IsBackground = true,
                    Name = "KageDesk.WakeUpServer"
                };
                _listenThread = thread;
                thread.Start();
            }
            catch (Exception ex)
            {
                LastListenException = ex;
                _listenThread = null;
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
            options: PipeOptions.None,
            inBufferSize: 64,
            outBufferSize: 64,
            pipeSecurity: security);
    }

    private void ListenLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = Interlocked.Exchange(ref _pendingServer, null) ?? CreateServerInstance();
                    Volatile.Write(ref _activeServer, server);
                    if (ct.IsCancellationRequested) break;

                    server.WaitForConnection();
                    if (ct.IsCancellationRequested) break;

                    HandleClient(server, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException) when (ct.IsCancellationRequested)
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
                    try
                    {
                        if (ct.WaitHandle.WaitOne(50)) break;
                    }
                    catch
                    {
                        break;
                    }
                }
                finally
                {
                    if (server != null)
                    {
                        Interlocked.CompareExchange(ref _activeServer, null, server);
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

    private void HandleClient(NamedPipeServerStream server, CancellationToken ct)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
        using var readCancelReg = readCts.Token.Register(() => SingleInstanceWakeUp.TryCancelPipeIo(server));

        // Read into an OpcodeLength + 1 buffer in message mode so any oversized message or
        // trailing bytes are detected and rejected BEFORE sending ACK.
        byte[] buf = new byte[SingleInstanceWakeUp.OpcodeLength + 1];
        int total = 0;
        bool messageComplete = false;
        while (total <= SingleInstanceWakeUp.OpcodeLength)
        {
            int n = server.Read(buf, total, buf.Length - total);
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

        // Validate connected peer's User SID (after reading opcode, as required by ImpersonateNamedPipeClient)
        // BEFORE dispatching wake-up or sending ACK. Compares User SID (not default token owner SID),
        // allowing same-user elevated <-> unelevated wakes while rejecting different users.
        SecurityIdentifier? peerUserSid = ResolveConnectedPeerUserSid(server);
        if (!SingleInstanceWakeUp.IsAuthorizedSameUserPeer(_expectedUserSid, peerUserSid))
        {
            return;
        }

        // Coalesce & rate-limit wake-up UI triggers (default: max 1 per 2s) BEFORE sending ACK
        // so receiving ACK guarantees the wake-up callback has already been dispatched.
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

        // Acknowledge valid opcode and wait briefly for client EOF so the client reads ACK before close.
        try
        {
            server.Write(AckBytes, 0, AckBytes.Length);
            server.Flush();

            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drainCts.CancelAfter(TimeSpan.FromMilliseconds(100));
            using var drainCancelReg = drainCts.Token.Register(() => SingleInstanceWakeUp.TryCancelPipeIo(server));
            byte[] eofProbe = new byte[1];
            try
            {
                _ = server.Read(eofProbe, 0, eofProbe.Length);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
        catch { }
    }

    private SecurityIdentifier? ResolveConnectedPeerUserSid(NamedPipeServerStream server)
    {
        if (_resolvePeerUserSidOverride != null)
        {
            return _resolvePeerUserSidOverride(server);
        }

        try
        {
            SecurityIdentifier? peerSid = null;
            server.RunAsClient(() =>
            {
                peerSid = WindowsIdentity.GetCurrent().User;
            });
            return peerSid;
        }
        catch
        {
            return null;
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
        Thread? listenThread;
        NamedPipeServerStream? unclaimed;
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            unclaimed = Interlocked.Exchange(ref _pendingServer, null);
            listenThread = _listenThread;
        }

        if (unclaimed != null)
        {
            try { unclaimed.Dispose(); } catch { }
        }

        SingleInstanceWakeUp.TryCancelPipeIo(Volatile.Read(ref _activeServer));

        bool completed = true;
        try
        {
            if (listenThread != null && listenThread != Thread.CurrentThread)
            {
                long deadline = Environment.TickCount64 + 2000;
                completed = false;
                while (true)
                {
                    if (listenThread.Join(25))
                    {
                        completed = true;
                        break;
                    }
                    SingleInstanceWakeUp.TryCancelPipeIo(Volatile.Read(ref _activeServer));
                    if (Environment.TickCount64 >= deadline)
                    {
                        break;
                    }
                }
            }
        }
        catch { }

        if (completed)
        {
            _cts.Dispose();
        }
    }
}
