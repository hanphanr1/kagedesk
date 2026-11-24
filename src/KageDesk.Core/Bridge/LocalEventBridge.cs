using System.Diagnostics;
using System.Net;
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
    private readonly DateTimeOffset _startedAt;
    private readonly CancellationTokenSource _cts = new();
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

    public LocalEventBridge(HqStateAggregator aggregator, int port = DefaultPort, AppConfig? config = null)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _config = config ?? new AppConfig();
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
                _ = Task.Run(() => HandleRequestSafeAsync(context));
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

    private async Task HandleRequestSafeAsync(HttpListenerContext context)
    {
        try
        {
            await ProcessRequestAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonAsync(context.Response, 500, new { ok = false, error = ex.Message }).ConfigureAwait(false);
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

        // 1. GET /api/health
        if (method == "GET" && string.Equals(path, "/api/health", StringComparison.OrdinalIgnoreCase))
        {
            var workingSet = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);
            var uptime = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds;
            var state = _aggregator.GetState();

            await WriteJsonAsync(response, 200, new
            {
                ok = true,
                version = "1.0.0",
                workingSetMb = Math.Round(workingSet, 2),
                uptimeSec = Math.Round(uptime, 1),
                activeTheme = _config.ActiveThemeId,
                agentCount = state.Agents.Count
            }).ConfigureAwait(false);
            return;
        }

        // 2. GET /api/state
        if (method == "GET" && string.Equals(path, "/api/state", StringComparison.OrdinalIgnoreCase))
        {
            var state = _aggregator.GetState();
            await WriteJsonAsync(response, 200, state).ConfigureAwait(false);
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

    private static async Task<(string? Body, bool IsTooLarge)> ReadBodySafeAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxBodyBytes)
        {
            return (null, true);
        }

        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int totalRead = 0;

        while (true)
        {
            int read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
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
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_listenTask != null)
        {
            try { await _listenTask.ConfigureAwait(false); } catch { }
        }
        GC.SuppressFinalize(this);
    }
}
