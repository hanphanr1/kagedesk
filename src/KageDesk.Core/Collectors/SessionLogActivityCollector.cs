using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using KageDesk.Core.Models;

namespace KageDesk.Core.Collectors;

public sealed record SessionWatchRoot(
    string Provider,
    string DisplayName,
    string DirectoryPath,
    string SearchPattern = "*.jsonl"
);

public sealed record DerivedSessionActivity(
    string Provider,
    string DisplayName,
    AgentState State,
    string? DerivedActionToken,
    DateTimeOffset ObservedAt
);

/// <summary>
/// Lightweight, privacy-safe local session/transcript log watcher.
/// Uses debounced FileSystemWatcher + bounded tail reads (&lt;= 8 KB) and extracts
/// ONLY derived non-sensitive activity state and canonical tool action tokens.
/// Zero prompt text, zero file paths, zero secrets are retained in memory or emitted.
/// </summary>
public sealed class SessionLogActivityCollector : IDisposable
{
    public const int MaxTailReadBytes = 8 * 1024;
    private const int MaxTrackedFiles = 64;

    private readonly IReadOnlyList<SessionWatchRoot> _roots;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _debounceWindow;
    private readonly TimeSpan _workingHoldDuration;
    private readonly TimeSpan _doneHoldDuration;
    private readonly TimeSpan _staleExpiryDuration;

    private readonly object _lock = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, (long Offset, DateTime LastWriteUtc)> _fileOffsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DerivedSessionActivity> _providerActivity = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _debounceTimer;
    private Timer? _expiryTimer;
    private bool _started;
    private bool _disposed;

    public event EventHandler<IReadOnlyList<AgentSnapshot>>? ActivityChanged;

    public SessionLogActivityCollector(
        IReadOnlyList<SessionWatchRoot>? watchRoots = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? debounceWindow = null,
        TimeSpan? workingHoldDuration = null,
        TimeSpan? doneHoldDuration = null,
        TimeSpan? staleExpiryDuration = null)
    {
        _roots = watchRoots ?? BuildDefaultWatchRoots();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _debounceWindow = debounceWindow ?? TimeSpan.FromMilliseconds(150);
        _workingHoldDuration = workingHoldDuration ?? TimeSpan.FromSeconds(20);
        _doneHoldDuration = doneHoldDuration ?? TimeSpan.FromSeconds(10);
        _staleExpiryDuration = staleExpiryDuration ?? TimeSpan.FromSeconds(90);
    }

    public static IReadOnlyList<SessionWatchRoot> BuildDefaultWatchRoots(string? userProfile = null, string? appData = null)
    {
        string home = string.IsNullOrWhiteSpace(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfile;
        string roaming = string.IsNullOrWhiteSpace(appData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : appData;

        return new[]
        {
            new SessionWatchRoot("claude", "Claude", Path.Combine(home, ".claude", "projects"), "*.jsonl"),
            new SessionWatchRoot("codex", "Codex", Path.Combine(home, ".codex", "sessions"), "*.jsonl"),
            new SessionWatchRoot("omp", "OMP", Path.Combine(home, ".omp", "agent", "sessions"), "*.jsonl"),
            new SessionWatchRoot("omp", "OMP", Path.Combine(home, ".pi", "agent", "sessions"), "*.jsonl"),
            new SessionWatchRoot("omp", "OMP", Path.Combine(roaming, "omp", "sessions"), "*.jsonl")
        };
    }

    public int ActiveWatcherCount
    {
        get
        {
            lock (_lock)
            {
                return _watchers.Count;
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _started) return;
            _started = true;

            foreach (var root in _roots)
            {
                if (string.IsNullOrWhiteSpace(root.DirectoryPath) || !Directory.Exists(root.DirectoryPath))
                    continue;

                try
                {
                    var watcher = new FileSystemWatcher(root.DirectoryPath, root.SearchPattern)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                        InternalBufferSize = 16384
                    };

                    string provider = root.Provider;
                    void OnFsEvent(object _, FileSystemEventArgs e)
                    {
                        if (_disposed || string.IsNullOrWhiteSpace(e.FullPath)) return;
                        EnqueuePendingFile(e.FullPath, provider);
                    }

                    watcher.Changed += OnFsEvent;
                    watcher.Created += OnFsEvent;
                    watcher.Renamed += (_, e) =>
                    {
                        if (_disposed || string.IsNullOrWhiteSpace(e.FullPath)) return;
                        EnqueuePendingFile(e.FullPath, provider);
                    };
                    watcher.Error += (_, _) => { /* Non-fatal watcher buffer overflow */ };
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch
                {
                    // Gracefully skip unreadable/missing root directories
                }
            }

            _debounceTimer = new Timer(_ => FlushPendingFilesSafe(), null, Timeout.Infinite, Timeout.Infinite);
            _expiryTimer = new Timer(_ => PruneAndPublishIfChanged(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_started) return;
            _started = false;

            foreach (var w in _watchers)
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
                catch { }
            }
            _watchers.Clear();

            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _expiryTimer?.Dispose();
            _expiryTimer = null;
            _pendingPaths.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>
    /// Synchronously processes a single file delta using bounded tail reading (&lt;= 8 KB).
    /// Useful for both live FileSystemWatcher flushes and deterministic unit tests.
    /// </summary>
    public DerivedSessionActivity? ProcessFileDelta(string filePath, string provider, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        byte[]? chunk = ReadBoundedTailDelta(filePath);
        if (chunk == null || chunk.Length == 0)
            return null;

        DateTimeOffset now = _clock();
        string resolvedDisplay = !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : FormatProviderDisplay(provider);

        var derived = ParseDerivedActivityFromTail(provider, resolvedDisplay, chunk, now);
        if (derived == null)
            return null;

        IReadOnlyList<AgentSnapshot> snapshots;
        lock (_lock)
        {
            _providerActivity[provider] = derived;
            snapshots = BuildCurrentSnapshotsLocked(now);
        }

        ActivityChanged?.Invoke(this, snapshots);
        return derived;
    }

    public IReadOnlyList<AgentSnapshot> GetCurrentSnapshots()
    {
        lock (_lock)
        {
            return BuildCurrentSnapshotsLocked(_clock());
        }
    }

    /// <summary>
    /// Reads at most <see cref="MaxTailReadBytes"/> bytes from the file's previous tracked offset
    /// (or tail of file if first read or delta exceeds 8 KB). Never loads large multi-MB transcripts into RAM.
    /// </summary>
    public byte[]? ReadBoundedTailDelta(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            if (length <= 0) return null;

            long startOffset;
            lock (_lock)
            {
                if (_fileOffsets.TryGetValue(filePath, out var prev) && prev.Offset <= length)
                {
                    if (prev.Offset == length)
                        return null;

                    long delta = length - prev.Offset;
                    startOffset = delta > MaxTailReadBytes ? length - MaxTailReadBytes : prev.Offset;
                }
                else
                {
                    startOffset = Math.Max(0, length - MaxTailReadBytes);
                }

                // Cap tracked file dictionary size
                if (_fileOffsets.Count >= MaxTrackedFiles && !_fileOffsets.ContainsKey(filePath))
                {
                    string? oldestKey = null;
                    DateTime oldestDt = DateTime.MaxValue;
                    foreach (var kv in _fileOffsets)
                    {
                        if (kv.Value.LastWriteUtc < oldestDt)
                        {
                            oldestDt = kv.Value.LastWriteUtc;
                            oldestKey = kv.Key;
                        }
                    }
                    if (oldestKey != null)
                        _fileOffsets.Remove(oldestKey);
                }
            }

            int toRead = (int)Math.Min(MaxTailReadBytes, length - startOffset);
            if (toRead <= 0) return null;

            fs.Seek(startOffset, SeekOrigin.Begin);
            byte[] buffer = new byte[toRead];
            int totalRead = 0;
            while (totalRead < toRead)
            {
                int read = fs.Read(buffer, totalRead, toRead - totalRead);
                if (read <= 0) break;
                totalRead += read;
            }

            lock (_lock)
            {
                _fileOffsets[filePath] = (startOffset + totalRead, DateTime.UtcNow);
            }

            if (totalRead == 0) return null;
            if (totalRead < buffer.Length)
            {
                Array.Resize(ref buffer, totalRead);
            }

            // If we started mid-file on a non-zero offset, drop any truncated leading partial line
            if (startOffset > 0)
            {
                int firstNewline = Array.IndexOf(buffer, (byte)'\n');
                if (firstNewline >= 0 && firstNewline + 1 < buffer.Length)
                {
                    int trimmedLen = buffer.Length - (firstNewline + 1);
                    byte[] trimmed = new byte[trimmedLen];
                    Buffer.BlockCopy(buffer, firstNewline + 1, trimmed, 0, trimmedLen);
                    return trimmed;
                }
            }

            return buffer;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses only non-sensitive derived state and canonical action token from bounded JSONL tail bytes.
    /// Zero prompt text, file paths, command arguments, or secrets are extracted or retained.
    /// </summary>
    public static DerivedSessionActivity? ParseDerivedActivityFromTail(
        string provider,
        string displayName,
        ReadOnlySpan<byte> tailBytes,
        DateTimeOffset observedAt)
    {
        if (tailBytes.IsEmpty)
            return null;

        string text = Encoding.UTF8.GetString(tailBytes);
        string[] lines = text.Split('\n');

        DerivedSessionActivity? latest = null;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length < 2 || line[0] != '{' || line[^1] != '}')
                continue;

            var parsed = TryParseSingleJsonLine(provider, displayName, line, observedAt);
            if (parsed != null)
            {
                latest = parsed;
            }
        }

        return latest;
    }

    private static DerivedSessionActivity? TryParseSingleJsonLine(
        string provider,
        string displayName,
        string jsonLine,
        DateTimeOffset observedAt)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string? typeStr = GetStringProp(root, "type") ?? GetStringProp(root, "event");
            string? roleStr = GetStringProp(root, "role");
            string? stopReason = GetStringProp(root, "stop_reason") ?? GetStringProp(root, "stopReason");
            string? rawToolName = GetStringProp(root, "tool_name") ?? GetStringProp(root, "toolName");

            // Check nested "message" object (Claude Code / Pi / OMP JSONL format)
            if (root.TryGetProperty("message", out var msgObj) && msgObj.ValueKind == JsonValueKind.Object)
            {
                roleStr ??= GetStringProp(msgObj, "role");
                stopReason ??= GetStringProp(msgObj, "stop_reason") ?? GetStringProp(msgObj, "stopReason");

                if (msgObj.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in contentArr.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        string? itemType = GetStringProp(item, "type");
                        if (string.Equals(itemType, "tool_use", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(itemType, "tool_call", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase))
                        {
                            rawToolName = GetStringProp(item, "name") ?? rawToolName;
                        }
                        else if (string.Equals(itemType, "thinking", StringComparison.OrdinalIgnoreCase) && rawToolName == null)
                        {
                            rawToolName = "thinking";
                        }
                    }
                }
            }

            // Check nested "payload" object (Codex JSONL format)
            if (root.TryGetProperty("payload", out var payloadObj) && payloadObj.ValueKind == JsonValueKind.Object)
            {
                string? payloadType = GetStringProp(payloadObj, "type");
                if (!string.IsNullOrEmpty(payloadType))
                {
                    typeStr = payloadType;
                }
                roleStr ??= GetStringProp(payloadObj, "role");
                rawToolName ??= GetStringProp(payloadObj, "name") ?? GetStringProp(payloadObj, "tool");
            }

            // 1. Waiting for user input / permission / approval
            if (IsApprovalEvent(typeStr, rawToolName, stopReason))
            {
                return new DerivedSessionActivity(provider, displayName, AgentState.NeedsApproval, "approve", observedAt);
            }

            // 2. Explicit tool use -> Working + sanitized canonical action token ONLY
            if (!string.IsNullOrWhiteSpace(rawToolName))
            {
                string canonicalToken = MapToolNameToCanonicalToken(rawToolName);
                return new DerivedSessionActivity(provider, displayName, AgentState.Working, canonicalToken, observedAt);
            }

            // 3. Explicit turn completion -> Done
            if (IsCompletionEvent(typeStr, stopReason))
            {
                return new DerivedSessionActivity(provider, displayName, AgentState.Done, null, observedAt);
            }

            // 4. Explicit idle/session end
            if (IsIdleEvent(typeStr))
            {
                return new DerivedSessionActivity(provider, displayName, AgentState.Idle, null, observedAt);
            }

            // 5. User prompt sent or assistant actively thinking/responding -> Working ("think")
            if (string.Equals(typeStr, "user", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(roleStr, "user", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeStr, "assistant", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(roleStr, "assistant", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeStr, "thinking", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeStr, "tool_result", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeStr, "function_call_output", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeStr, "agent_reasoning", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeStr, "response_item", StringComparison.OrdinalIgnoreCase))
            {
                return new DerivedSessionActivity(provider, displayName, AgentState.Working, "think", observedAt);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps arbitrary tool names strictly to one of the fixed non-sensitive canonical action tokens
    /// so raw tool paths, MCP server identifiers, or secrets never enter KageDesk state.
    /// </summary>
    public static string MapToolNameToCanonicalToken(string rawToolName)
    {
        var (kind, _) = AgentActionClassifier.Classify(AgentState.Working, rawToolName);
        return kind switch
        {
            AgentActionKind.Reading => "read",
            AgentActionKind.Coding => "code",
            AgentActionKind.Executing => "exec",
            AgentActionKind.Testing => "test",
            AgentActionKind.Building => "exec",
            AgentActionKind.GitOps => "git",
            AgentActionKind.Searching => "scan",
            AgentActionKind.Reviewing => "read",
            _ => "think"
        };
    }

    private static bool IsApprovalEvent(string? typeStr, string? toolName, string? stopReason)
    {
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            if (toolName.Contains("ask_user", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("ask_question", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("askuserquestion", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("request_approval", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(typeStr))
        {
            if (typeStr.Equals("permission_request", StringComparison.OrdinalIgnoreCase) ||
                typeStr.Equals("approval_required", StringComparison.OrdinalIgnoreCase) ||
                typeStr.Equals("waiting_for_input", StringComparison.OrdinalIgnoreCase) ||
                typeStr.Equals("needs_approval", StringComparison.OrdinalIgnoreCase) ||
                typeStr.Equals("input_required", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(stopReason) &&
            stopReason.Equals("waiting_for_input", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsCompletionEvent(string? typeStr, string? stopReason)
    {
        if (!string.IsNullOrWhiteSpace(stopReason) &&
            (stopReason.Equals("end_turn", StringComparison.OrdinalIgnoreCase) ||
             stopReason.Equals("stop", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(typeStr) &&
            (typeStr.Equals("turn_complete", StringComparison.OrdinalIgnoreCase) ||
             typeStr.Equals("task_complete", StringComparison.OrdinalIgnoreCase) ||
             typeStr.Equals("response.completed", StringComparison.OrdinalIgnoreCase) ||
             typeStr.Equals("done", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool IsIdleEvent(string? typeStr)
    {
        return !string.IsNullOrWhiteSpace(typeStr) &&
               (typeStr.Equals("session_idle", StringComparison.OrdinalIgnoreCase) ||
                typeStr.Equals("idle", StringComparison.OrdinalIgnoreCase) ||
                typeStr.Equals("session_end", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetStringProp(JsonElement obj, string propName)
    {
        if (obj.TryGetProperty(propName, out var el) && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }
        return null;
    }

    private void EnqueuePendingFile(string fullPath, string provider)
    {
        _pendingPaths[fullPath] = provider;
        lock (_lock)
        {
            if (_disposed || _debounceTimer == null) return;
            try
            {
                _debounceTimer.Change(_debounceWindow, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException) { }
        }
    }

    private void FlushPendingFilesSafe()
    {
        if (_disposed || _pendingPaths.IsEmpty) return;

        var batch = new List<(string Path, string Provider)>();
        foreach (var kv in _pendingPaths)
        {
            if (_pendingPaths.TryRemove(kv.Key, out var provider))
            {
                batch.Add((kv.Key, provider));
            }
        }

        if (batch.Count == 0) return;

        bool updated = false;
        DateTimeOffset now = _clock();

        foreach (var (path, provider) in batch)
        {
            byte[]? chunk = ReadBoundedTailDelta(path);
            if (chunk == null || chunk.Length == 0) continue;

            var derived = ParseDerivedActivityFromTail(provider, FormatProviderDisplay(provider), chunk, now);
            if (derived != null)
            {
                lock (_lock)
                {
                    _providerActivity[provider] = derived;
                }
                updated = true;
            }
        }

        if (updated)
        {
            IReadOnlyList<AgentSnapshot> snapshots;
            lock (_lock)
            {
                snapshots = BuildCurrentSnapshotsLocked(now);
            }
            ActivityChanged?.Invoke(this, snapshots);
        }
    }

    private void PruneAndPublishIfChanged()
    {
        if (_disposed) return;

        DateTimeOffset now = _clock();
        IReadOnlyList<AgentSnapshot>? snapshotsToPublish = null;

        lock (_lock)
        {
            if (_providerActivity.Count == 0) return;

            bool changed = false;
            var toRemove = new List<string>();
            foreach (var (provider, act) in _providerActivity)
            {
                TimeSpan age = now - act.ObservedAt;
                if (age > _staleExpiryDuration)
                {
                    toRemove.Add(provider);
                    changed = true;
                }
                else if (act.State == AgentState.Working && age > _workingHoldDuration)
                {
                    _providerActivity[provider] = act with { State = AgentState.Idle, DerivedActionToken = null };
                    changed = true;
                }
                else if (act.State == AgentState.Done && age > _doneHoldDuration)
                {
                    _providerActivity[provider] = act with { State = AgentState.Idle, DerivedActionToken = null };
                    changed = true;
                }
            }

            foreach (var k in toRemove)
            {
                _providerActivity.Remove(k);
            }

            if (changed)
            {
                snapshotsToPublish = BuildCurrentSnapshotsLocked(now);
            }
        }

        if (snapshotsToPublish != null)
        {
            ActivityChanged?.Invoke(this, snapshotsToPublish);
        }
    }

    private IReadOnlyList<AgentSnapshot> BuildCurrentSnapshotsLocked(DateTimeOffset now)
    {
        if (_providerActivity.Count == 0)
            return Array.Empty<AgentSnapshot>();

        var result = new List<AgentSnapshot>(_providerActivity.Count);
        foreach (var (provider, act) in _providerActivity)
        {
            TimeSpan age = now - act.ObservedAt;
            if (age > _staleExpiryDuration) continue;

            AgentState effectiveState = act.State;
            string? effectiveTool = act.DerivedActionToken;

            if (effectiveState == AgentState.Working && age > _workingHoldDuration)
            {
                effectiveState = AgentState.Idle;
                effectiveTool = null;
            }
            else if (effectiveState == AgentState.Done && age > _doneHoldDuration)
            {
                effectiveState = AgentState.Idle;
                effectiveTool = null;
            }

            result.Add(new AgentSnapshot(
                Id: $"session:{provider}",
                Name: act.DisplayName,
                Role: AgentRole.External,
                SpriteKey: "generic",
                State: effectiveState,
                ToolName: effectiveTool,
                LastActivityAt: act.ObservedAt,
                UpdatedAt: act.ObservedAt,
                Source: "session-log"
            ));
        }

        return result;
    }

    private static string FormatProviderDisplay(string provider)
    {
        return provider.Trim().ToLowerInvariant() switch
        {
            "claude" => "Claude",
            "codex" => "Codex",
            "omp" => "OMP",
            "opencode" => "OpenCode",
            _ => AgentAppearanceRegistry.FormatProviderDisplayName(provider)
        };
    }
}
