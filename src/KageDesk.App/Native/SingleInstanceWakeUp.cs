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
        return TrySignalRunningInstanceCoreAsync(
                pipeName,
                maxRetryWindow,
                expectedUserSid,
                resolveServerOwnerSid,
                onAttemptFailure)
            .GetAwaiter()
            .GetResult();
    }

    internal static async Task<bool> TrySignalRunningInstanceCoreAsync(
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
        if (budget <= TimeSpan.Zero)
        {
            return false;
        }

        long startTick = Environment.TickCount64;
        byte[] ackBuf = new byte[1];

        // ONE single absolute deadline governs the ENTIRE client retry + connect + write + flush + read-ACK exchange.
        using var deadlineCts = new CancellationTokenSource(budget);
        CancellationToken ct = deadlineCts.Token;

        while (!ct.IsCancellationRequested)
        {
            string stage = "init";

            try
            {
                // Do NOT set PipeOptions.CurrentUserOnly (dotnet/runtime#123903 breaks same-user
                // elevated <-> unelevated pipes). Use PipeOptions.Asynchronous for true overlapped
                // kernel I/O cancellation bound directly to the single absolute deadline token,
                // while TokenImpersonationLevel.Identification lets the server verify our User SID via RunAsClient.
                await using var client = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: targetPipe,
                    direction: PipeDirection.InOut,
                    options: PipeOptions.Asynchronous,
                    impersonationLevel: TokenImpersonationLevel.Identification);

                stage = "connect";
                await client.ConnectAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

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

                stage = "write";
                await client.WriteAsync(WakeOpcodeBytes, ct).ConfigureAwait(false);
                stage = "flush";
                await client.FlushAsync(ct).ConfigureAwait(false);

                stage = "read";
                int read = await client.ReadAsync(ackBuf, ct).ConfigureAwait(false);
                if (read == 1 && ackBuf[0] == AckByte)
                {
                    return true;
                }

                onAttemptFailure?.Invoke($"t={Environment.TickCount64 - startTick}ms bad-ack read={read} byte={ackBuf[0]}");
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                onAttemptFailure?.Invoke($"t={Environment.TickCount64 - startTick}ms @{stage} DeadlineExceeded: {ex.Message}");
                return false;
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

            if (ct.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

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

            // Capture token before starting listener task so Dispose() cannot race on _cts.Token.
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
                _listenTask = Task.Run(() => ListenLoopAsync(ct));
            }
            catch (Exception ex)
            {
                LastListenException = ex;
                _listenTask = null;
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
                    if (ct.IsCancellationRequested) break;

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;

                    await HandleClientAsync(server, ct).ConfigureAwait(false);
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
                        await Task.Delay(50, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
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
        using var exchangeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        exchangeCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
        CancellationToken exchangeCt = exchangeCts.Token;

        // Read into an OpcodeLength + 1 buffer in message mode so any oversized message or
        // trailing bytes are detected and rejected BEFORE sending ACK.
        byte[] buf = new byte[SingleInstanceWakeUp.OpcodeLength + 1];
        int total = 0;
        bool messageComplete = false;
        while (total <= SingleInstanceWakeUp.OpcodeLength)
        {
            int n = await server.ReadAsync(buf.AsMemory(total, buf.Length - total), exchangeCt).ConfigureAwait(false);
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
            await server.WriteAsync(AckBytes, exchangeCt).ConfigureAwait(false);
            await server.FlushAsync(exchangeCt).ConfigureAwait(false);

            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drainCts.CancelAfter(TimeSpan.FromMilliseconds(100));
            byte[] eofProbe = new byte[1];
            try
            {
                _ = await server.ReadAsync(eofProbe, drainCts.Token).ConfigureAwait(false);
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
        if (listenTask != null)
        {
            try
            {
                completed = listenTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                completed = listenTask.IsCompleted;
            }
        }

        if (completed)
        {
            _cts.Dispose();
        }
    }
}
