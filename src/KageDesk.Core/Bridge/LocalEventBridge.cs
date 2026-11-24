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
    private readonly ConcurrentDictionary<Task, byte> _activeRequests = new();

    private Task? _listenTask;
    private bool _disposed;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/";
    public bool IsRunning => _listener.IsListening;

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
        Func<string>? getActiveThemeId = null)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _config = config ?? new AppConfig();
        _requiredToken = requiredToken;
        _getActiveThemeId = getActiveThemeId;
        Port = port;
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
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);

                // Bounded concurrency check: max 16 concurrent requests
                if (!_concurrencySemaphore.Wait(0))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await WriteJsonAsync(context.Response, 503, new { ok = false, error = "Service Unavailable: concurrency limit reached" }).ConfigureAwait(false);
                        }
                        catch { }
                    });
                    continue;
                }

                var requestTask = ProcessTrackedRequestAsync(context);
                _activeRequests.TryAdd(requestTask, 0);
                _ = requestTask.ContinueWith(t => _activeRequests.TryRemove(t, out _));
            }
            catch (HttpListenerException) when (_cts.IsCancellationRequested || !_listener.IsListening)
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

    private async Task ProcessTrackedRequestAsync(HttpListenerContext context)
    {
        try
        {
            await HandleRequestSafeAsync(context).ConfigureAwait(false);
        }
        finally
        {
            _concurrencySemaphore.Release();
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
                version = "1.0.0",
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

        // 2. GET /api/state (Omit raw free-text prompt/lastMessage)
        if (method == "GET" && string.Equals(path, "/api/state", StringComparison.OrdinalIgnoreCase))
        {
            var state = _aggregator.GetState();

            // Sanitize sensitive fields (remove LastMessage from external HTTP response)
            var sanitizedAgents = new List<AgentSnapshot>(state.Agents.Count);
            foreach (var a in state.Agents)
            {
                sanitizedAgents.Add(a with
                {
                    LastMessage = null
                });
            }

            var safeState = state with { Agents = sanitizedAgents };
            await WriteJsonAsync(response, 200, safeState).ConfigureAwait(false);
            return;
        }

        // 3. POST /api/event
        if (method == "POST" && string.Equals(path, "/api/event", StringComparison.OrdinalIgnoreCase))
        {
            var (body, isTooLarge) = await ReadBodySafeAsync(request).ConfigureAwait(false);
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

    private async Task<(string? Body, bool IsTooLarge)> ReadBodySafeAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxBodyBytes)
        {
            return (null, true);
        }

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        readCts.CancelAfter(TimeSpan.FromSeconds(5));

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
        var tasks = _activeRequests.Keys.ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(timeout)).ConfigureAwait(false);
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
            if (_listener.IsListening)
                _listener.Stop();
            _listener.Close();
        }
        catch { }

        _cts.Dispose();
        _concurrencySemaphore.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        // Drain in-flight requests with bounded timeout
        await DrainActiveRequestsAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        Dispose();
        if (_listenTask != null)
        {
            try { await _listenTask.ConfigureAwait(false); } catch { }
        }
        GC.SuppressFinalize(this);
    }
}
