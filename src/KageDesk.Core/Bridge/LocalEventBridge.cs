using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KageDesk.Core.Models;
using KageDesk.Core.State;

namespace KageDesk.Core.Bridge;

public class LocalEventBridge : IDisposable, IAsyncDisposable
{
    public const int DefaultPort = 4319;
    public const int MaxBodyBytes = 32 * 1024; // 32 KB

    private readonly HttpListener _listener;
    private readonly HqStateAggregator _aggregator;
    private readonly AppConfig _config;
    private readonly string? _requiredToken;
    private readonly Func<string>? _getActiveThemeId;
    private readonly DateTimeOffset _startedAt;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _concurrencySemaphore = new(16, 16);
    private readonly ConcurrentDictionary<Task, byte> _inFlightTasks = new();
    private readonly ConcurrentDictionary<HttpListenerContext, byte> _activeContexts = new();
    private readonly object _shutdownLock = new();
    private Task? _shutdownTask;

    private Task? _listenTask;
    private volatile bool _stopping;
    private bool _disposed;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/";
    public bool IsRunning => _listener.IsListening;
    public TimeSpan RequestReadTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public LocalEventBridge(
        HqStateAggregator aggregator,
        int port = DefaultPort,
        AppConfig? config = null,
        string? requiredToken = null,
        Func<string>? getActiveThemeId = null,
        TimeSpan? requestReadTimeout = null)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _config = config ?? new AppConfig();
        _requiredToken = requiredToken;
        _getActiveThemeId = getActiveThemeId;
        Port = port;
        if (requestReadTimeout.HasValue)
        {
            RequestReadTimeout = requestReadTimeout.Value;
        }
        _startedAt = DateTimeOffset.UtcNow;

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalEventBridge));
        if (_listener.IsListening) return;

        _listener.Start();
        _listenTask = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested && !_stopping && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);

                // Bounded concurrency check: max 16 concurrent requests
                if (!_concurrencySemaphore.Wait(0))
                {
                    var saturatedTask = RespondSaturated503Async(context);
                    _inFlightTasks.TryAdd(saturatedTask, 0);
                    _ = saturatedTask.ContinueWith(t => _inFlightTasks.TryRemove(t, out _), TaskScheduler.Default);
                    continue;
                }

                var requestTask = ProcessTrackedRequestAsync(context);
                _inFlightTasks.TryAdd(requestTask, 0);
                _ = requestTask.ContinueWith(t => _inFlightTasks.TryRemove(t, out _), TaskScheduler.Default);
            }
            catch (HttpListenerException) when (_cts.IsCancellationRequested || _stopping || !_listener.IsListening)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                // Continue listening
            }
        }
    }

    private static async Task RespondSaturated503Async(HttpListenerContext context)
    {
        try
        {
            await WriteJsonAsync(context.Response, 503, new { ok = false, error = "Service Unavailable: concurrency limit reached" }).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task ProcessTrackedRequestAsync(HttpListenerContext context)
    {
        _activeContexts.TryAdd(context, 0);
        try
        {
            await HandleRequestSafeAsync(context).ConfigureAwait(false);
        }
        finally
        {
            _activeContexts.TryRemove(context, out _);
            try
            {
                _concurrencySemaphore.Release();
            }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }
    }

    private async Task HandleRequestSafeAsync(HttpListenerContext context)
    {
        try
        {
            await ProcessRequestAsync(context).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                // Never leak internal exception details to client
                await WriteJsonAsync(context.Response, 500, new { ok = false, error = "Internal server error" }).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        // Security check: loopback only
        if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address))
        {
            response.StatusCode = 403;
            response.Close();
            return;
        }

        var path = request.Url?.AbsolutePath ?? "/";
        var method = request.HttpMethod.ToUpperInvariant();

        // 1. GET /api/health (Non-sensitive: no token required)
        if (method == "GET" && string.Equals(path, "/api/health", StringComparison.OrdinalIgnoreCase))
        {
            var workingSet = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);
            var uptime = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds;
            var state = _aggregator.GetState();
            string activeTheme = _getActiveThemeId?.Invoke() ?? _config.ActiveThemeId;

            await WriteJsonAsync(response, 200, new
            {
                ok = true,
                version = "2.0.0",
                workingSetMb = Math.Round(workingSet, 2),
                uptimeSec = Math.Round(uptime, 1),
                activeTheme = activeTheme,
                agentCount = state.Agents.Count
            }).ConfigureAwait(false);
            return;
        }

        // Token authentication check for all other endpoints
        if (!IsAuthorized(request))
        {
            await WriteJsonAsync(response, 401, new { ok = false, error = "Unauthorized: missing or invalid X-KageDesk-Token header" }).ConfigureAwait(false);
            return;
        }

        // 2. GET /api/state (Allowlisted DTO: strictly omit Prompt, LastMessage, QuestionPrompt, and event Message)
        if (method == "GET" && string.Equals(path, "/api/state", StringComparison.OrdinalIgnoreCase))
        {
            var state = _aggregator.GetState();

            var publicAgents = new List<PublicAgentDto>(state.Agents.Count);
            foreach (var a in state.Agents)
            {
                publicAgents.Add(new PublicAgentDto(
                    Id: a.Id,
                    Name: a.Name,
                    Role: a.Role,
                    SpriteKey: a.SpriteKey,
                    State: a.State,
                    ToolName: a.ToolName,
                    TaskTitle: a.TaskTitle,
                    Repo: a.Repo,
                    Branch: a.Branch,
                    IsBotHome: a.IsBotHome,
                    Unread: a.Unread,
                    Source: a.Source,
                    LastActivityAt: a.LastActivityAt,
                    UpdatedAt: a.UpdatedAt,
                    WorkStartedAt: a.WorkStartedAt
                ));
            }

            var publicEvents = new List<PublicVisualEventDto>(state.PendingVisualEvents.Count);
            foreach (var e in state.PendingVisualEvents)
            {
                publicEvents.Add(new PublicVisualEventDto(
                    SequenceId: e.SequenceId,
                    Type: e.Type,
                    AgentId: e.AgentId,
                    Timestamp: e.Timestamp,
                    TargetAgentId: e.TargetAgentId
                ));
            }

            var publicAccounts = state.Accounts.Select(a => new PublicAccountDto(
                Provider: a.Provider,
                AccountId: a.AccountId,
                Label: a.Label,
                IsActive: a.IsActive,
                QuotaPercent: a.QuotaPercent,
                QuotaResetAt: a.QuotaResetAt
            )).ToList();

            var publicBuilds = state.Builds.Select(b => new PublicBuildDto(
                Id: b.Id,
                Repo: b.Repo,
                WorkflowName: b.WorkflowName,
                Branch: b.Branch,
                Status: b.Status,
                Conclusion: b.Conclusion,
                CreatedAt: b.CreatedAt,
                UpdatedAt: b.UpdatedAt
            )).ToList();

            var publicState = new PublicHqStateDto(
                Agents: publicAgents,
                Accounts: publicAccounts,
                Builds: publicBuilds,
                Weather: state.Weather,
                Mood: state.Mood,
                PendingVisualEvents: publicEvents,
                DemoScenario: state.DemoScenario,
                Timestamp: state.Timestamp
            );

            await WriteJsonAsync(response, 200, publicState).ConfigureAwait(false);
            return;
        }

        // 3. POST /api/event
        if (method == "POST" && string.Equals(path, "/api/event", StringComparison.OrdinalIgnoreCase))
        {
            var (body, isTooLarge) = await ReadBodySafeAsync(context).ConfigureAwait(false);
            if (isTooLarge)
            {
                await WriteJsonAsync(response, 413, new { ok = false, error = "Payload Too Large: max 32KB allowed" }).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                await WriteJsonAsync(response, 400, new { ok = false, error = "Request body is empty" }).ConfigureAwait(false);
                return;
            }

            PushedEventPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<PushedEventPayload>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                await WriteJsonAsync(response, 400, new { ok = false, error = $"Invalid JSON: {ex.Message}" }).ConfigureAwait(false);
                return;
            }

            if (payload == null)
            {
                await WriteJsonAsync(response, 400, new { ok = false, error = "Invalid event payload" }).ConfigureAwait(false);
                return;
            }

            if (string.Equals(payload.Type, "handoff", StringComparison.OrdinalIgnoreCase))
            {
                var fromId = (payload.FromAgentId ?? payload.AgentId)?.Trim();
                var toId = payload.ToAgentId?.Trim();

                if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(toId))
                {
                    await WriteJsonAsync(response, 400, new { ok = false, error = "Handoff requires both fromAgentId and toAgentId" }).ConfigureAwait(false);
                    return;
                }

                if (fromId.Length > 64 || toId.Length > 64 ||
                    !IsValidEndpointId(fromId) || !IsValidEndpointId(toId))
                {
                    await WriteJsonAsync(response, 400, new { ok = false, error = "Invalid endpoint ID format: must be 1-64 characters matching ^[A-Za-z0-9_.:-]+$" }).ConfigureAwait(false);
                    return;
                }

                // Capture ONE snapshot and resolve BOTH endpoints with strict 2-pass priority
                HqState snapshot = _aggregator.GetState();
                if (AgentAliasResolver.AreSameCanonicalEndpoint(
                    fromId,
                    toId,
                    snapshot,
                    out string resolvedFrom,
                    out string resolvedTo,
                    out bool fromIsConcreteId,
                    out bool toIsConcreteId))
                {
                    await WriteJsonAsync(response, 400, new { ok = false, error = "fromAgentId and toAgentId cannot resolve to the same agent endpoint" }).ConfigureAwait(false);
                    return;
                }

                payload = payload with
                {
                    FromAgentId = resolvedFrom,
                    ToAgentId = resolvedTo,
                    FromIsConcreteId = fromIsConcreteId,
                    ToIsConcreteId = toIsConcreteId
                };
            }

            _aggregator.UpdatePushedEvent(payload);
            await WriteJsonAsync(response, 200, new { ok = true }).ConfigureAwait(false);
            return;
        }

        // 4. POST /api/demo/{scenario}
        if (method == "POST" && path.StartsWith("/api/demo/", StringComparison.OrdinalIgnoreCase))
        {
            var scenario = path.Substring("/api/demo/".Length);
            try
            {
                _aggregator.SetDemoScenario(scenario);
                await WriteJsonAsync(response, 200, new { ok = true, scenario }).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteJsonAsync(response, 400, new { ok = false, error = ex.Message }).ConfigureAwait(false);
            }
            return;
        }

        // 404 Not Found
        await WriteJsonAsync(response, 404, new { ok = false, error = "Not found" }).ConfigureAwait(false);
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (string.IsNullOrEmpty(_requiredToken)) return true;

        string? tokenHeader = request.Headers["X-KageDesk-Token"];
        if (string.IsNullOrEmpty(tokenHeader)) return false;

        byte[] expectedBytes = Encoding.UTF8.GetBytes(_requiredToken);
        byte[] actualBytes = Encoding.UTF8.GetBytes(tokenHeader);

        if (expectedBytes.Length != actualBytes.Length) return false;
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private async Task<(string? Body, bool IsTooLarge)> ReadBodySafeAsync(HttpListenerContext context)
    {
        var request = context.Request;
        if (request.ContentLength64 > MaxBodyBytes)
        {
            return (null, true);
        }

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        readCts.CancelAfter(RequestReadTimeout);
        using var reg = readCts.Token.Register(() =>
        {
            try { context.Response.Abort(); } catch { }
        });

        try
        {
            using var ms = new MemoryStream();
            var buffer = new byte[4096];
            int totalRead = 0;

            while (true)
            {
                int read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length, readCts.Token).ConfigureAwait(false);
                if (read == 0) break;

                totalRead += read;
                if (totalRead > MaxBodyBytes)
                {
                    return (null, true);
                }

                ms.Write(buffer, 0, read);
            }

            var encoding = request.ContentEncoding ?? Encoding.UTF8;
            return (encoding.GetString(ms.ToArray()), false);
        }
        catch (OperationCanceledException)
        {
            return (null, false);
        }
        catch (HttpListenerException)
        {
            return (null, false);
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object data)
    {
        try
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";

            var bytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            response.OutputStream.Close();
        }
        catch { }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    public async Task DrainActiveRequestsAsync(TimeSpan timeout)
    {
        var tasks = _inFlightTasks.Keys.ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                var whenAll = Task.WhenAll(tasks);
                var delay = Task.Delay(timeout);
                await Task.WhenAny(whenAll, delay).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private Task ShutdownCoreAsync(TimeSpan timeout)
    {
        lock (_shutdownLock)
        {
            if (_shutdownTask != null)
                return _shutdownTask;

            _shutdownTask = PerformShutdownAsync(timeout);
            return _shutdownTask;
        }
    }

    private async Task PerformShutdownAsync(TimeSpan timeout)
    {
        _stopping = true;
        _cts.Cancel();

        // 1. Stop listener so no new requests admitted
        try
        {
            if (_listener.IsListening)
                _listener.Stop();
        }
        catch { }

        // 2. Await accept loop task completion
        if (_listenTask != null)
        {
            try { await _listenTask.ConfigureAwait(false); } catch { }
        }

        // 3. Snapshot and wait up to bounded timeout for in-flight tasks
        var tasks = _inFlightTasks.Keys.ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                var whenAll = Task.WhenAll(tasks);
                var delay = Task.Delay(timeout);
                var finished = await Task.WhenAny(whenAll, delay).ConfigureAwait(false);
                if (finished != whenAll)
                {
                    // If still running after timeout, call Response.Abort() on active contexts
                    foreach (var ctx in _activeContexts.Keys)
                    {
                        try { ctx.Response.Abort(); } catch { }
                    }
                    // Await completion of ALL tracked tasks BEFORE disposing concurrency gate
                    try { await whenAll.ConfigureAwait(false); } catch { }
                }
            }
            catch { }
        }

        // 4. Safely close listener and dispose gate only after all admitted handlers exited
        try { _listener.Close(); } catch { }
        _concurrencySemaphore.Dispose();
        _cts.Dispose();
        _disposed = true;
    }

    public Task StopAsync(TimeSpan timeout = default)
    {
        TimeSpan drainTimeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(2);
        return ShutdownCoreAsync(drainTimeout);
    }

    public void Dispose()
    {
        try
        {
            ShutdownCoreAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownCoreAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static readonly System.Text.RegularExpressions.Regex ValidEndpointRegex =
        new(@"^[A-Za-z0-9_.:-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsValidEndpointId(string id)
    {
        return !string.IsNullOrWhiteSpace(id) && id.Length <= 64 && ValidEndpointRegex.IsMatch(id);
    }

    public static string ResolveCanonicalAgentId(string agentRef, HqState snapshot)
    {
        return AgentAliasResolver.ResolveCanonicalAgentId(agentRef, snapshot);
    }
}
