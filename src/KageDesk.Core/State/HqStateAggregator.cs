using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using KageDesk.Core.Models;

namespace KageDesk.Core.State;

public record PushedEventPayload(
    string? AgentId = null,
    string? Name = null,
    AgentRole? Role = null,
    string? Provider = null,
    AgentState? State = null,
    string? Type = null,
    string? ToolName = null,
    string? Message = null,
    string? Repo = null,
    string? BuildStatus = null,
    string? AccountProvider = null,
    string? AccountName = null,
    double? QuotaPercent = null,
    double? TtlSeconds = null,
    string? FromAgentId = null,
    string? ToAgentId = null,
    [property: JsonIgnore] bool FromIsConcreteId = false,
    [property: JsonIgnore] bool ToIsConcreteId = false
);

public class HqStateAggregator
{
    private const int MaxCappedItems = 32;
    public const int MaxVisualEvents = 16;
    public const int MaxMessageLength = 200;
    public const double MinVisualEventTtlSeconds = 1.0;
    public const double MaxVisualEventTtlSeconds = 30.0;
    public const double MinPushedEntityTtlSeconds = 1.0;
    public const double MaxPushedEntityTtlSeconds = 300.0;

    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _timeProvider;
    private readonly AppConfig _config;

    private long _sequenceCounter;
    private long _revisionCounter;

    private readonly Dictionary<string, AgentSnapshot> _orcaAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (AgentSnapshot Agent, DateTimeOffset ExpiresAt)> _pushedAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (AgentSnapshot Agent, DateTimeOffset ExpiresAt)> _sessionLogAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AgentSnapshot> _processAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AccountInfo> _orcaAccounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (AccountInfo Account, DateTimeOffset ExpiresAt)> _pushedAccounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (BuildInfo Build, DateTimeOffset ExpiresAt)> _pushedBuilds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BuildInfo> _collectorBuilds = new(StringComparer.OrdinalIgnoreCase);
    private long _ciGeneration;
    private long _processGeneration;
    private long _sessionLogGeneration;

    private readonly List<HqEvent> _visualEvents = new();
    private readonly Dictionary<string, (AgentSnapshot LastSnapshot, DateTimeOffset EnteredAt)> _departedDoneAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _doneStateEnteredAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _workStartedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastWorkingFinishedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastWorkingStartedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly AgentAppearanceRegistry _appearanceRegistry;

    private string? _demoScenario;
    private bool _demoHandoffEmitted;
    private bool _demoDramaEmitted;
    private bool _demoCelebrateEmitted;
    private DateTimeOffset _demoAnchorTime;
    private HqState _currentState;

    public event EventHandler<HqState>? StateChanged;

    public HqStateAggregator(AppConfig? config = null, Func<DateTimeOffset>? timeProvider = null, int? appearanceSeed = null)
    {
        _config = config ?? new AppConfig();
        _timeProvider = timeProvider ?? (() => DateTimeOffset.UtcNow);
        _appearanceRegistry = new AgentAppearanceRegistry(appearanceSeed);

        _currentState = new HqState(
            Agents: Array.Empty<AgentSnapshot>(),
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: ComputeEffectiveWeather(_config.WeatherMode, Array.Empty<BuildInfo>(), _timeProvider()),
            Mood: HqMood.Calm,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            DemoScenario: null,
            Timestamp: _timeProvider()
        );
    }

    public HqState GetState()
    {
        lock (_lock)
        {
            return _currentState;
        }
    }

    public HqState CurrentState => GetState();

    public void SetWeatherMode(WeatherMode mode)
    {
        HqState? toEmit = null;
        lock (_lock)
        {
            _config.WeatherMode = mode;
            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }
    }

    public void SetDemoScenario(string? scenario)
    {
        HqState? toEmit = null;
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(scenario) || string.Equals(scenario, "clear", StringComparison.OrdinalIgnoreCase))
            {
                _demoScenario = null;
                _demoHandoffEmitted = false;
                _demoDramaEmitted = false;
                _demoCelebrateEmitted = false;
            }
            else
            {
                var lower = scenario.Trim().ToLowerInvariant();
                if (lower is not ("working" or "approve" or "done" or "fail" or "clone" or "handoff" or "variety" or "mixi" or "drama" or "celebrate"))
                {
                    throw new ArgumentException($"Unknown demo scenario: '{scenario}'", nameof(scenario));
                }

                if (!string.Equals(_demoScenario, lower, StringComparison.Ordinal))
                {
                    _demoScenario = lower;
                    _demoHandoffEmitted = false;
                    _demoDramaEmitted = false;
                    _demoCelebrateEmitted = false;
                    _demoAnchorTime = _timeProvider();
                }
            }

            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }
    }

    public void UpdateOrcaSnapshot(IReadOnlyList<AgentSnapshot> agents, IReadOnlyList<AccountInfo> accounts)
    {
        HqState? toEmit = null;
        lock (_lock)
        {
            var now = _timeProvider();

            // Check previous orca agents for departures in Done state
            foreach (var (prevId, prevAgent) in _orcaAgents)
            {
                bool stillPresent = agents.Any(a => string.Equals(a.Id, prevId, StringComparison.OrdinalIgnoreCase));
                if (!stillPresent && prevAgent.State == AgentState.Done)
                {
                    DateTimeOffset enteredAt = _doneStateEnteredAt.TryGetValue(prevId, out var dt) ? dt : now;
                    _departedDoneAgents[prevId] = (prevAgent, enteredAt);
                }
            }

            _orcaAgents.Clear();
            foreach (var a in agents)
            {
                _orcaAgents[a.Id] = a;
                // If now in live agents, remove from departed
                _departedDoneAgents.Remove(a.Id);
            }

            // Cleanly replace Orca accounts on snapshot poll
            _orcaAccounts.Clear();
            foreach (var acc in accounts)
            {
                var key = $"{acc.Provider}:{acc.AccountId}";
                _orcaAccounts[key] = acc;
            }

            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }
    }

    public void ClearOrcaEntries()
    {
        HqState? toEmit = null;
        lock (_lock)
        {
            _orcaAgents.Clear();
            _orcaAccounts.Clear();
            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }
    }

    public long DisableAndClearProcessAgents()
    {
        HqState? toEmit = null;
        long newGen;
        lock (_lock)
        {
            newGen = ++_processGeneration;
            _processAgents.Clear();
            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }

        return newGen;
    }

    public long GetProcessGeneration()
    {
        lock (_lock)
        {
            return _processGeneration;
        }
    }

    public bool UpdateProcessAgents(IReadOnlyList<AgentSnapshot> agents, long? expectedGeneration = null)
    {
        HqState? toEmit = null;
        lock (_lock)
        {
            if (expectedGeneration.HasValue && expectedGeneration.Value != _processGeneration)
            {
                return false;
            }

            _processAgents.Clear();
            foreach (var a in agents)
            {
                _processAgents[a.Id] = a;
            }

            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }

        return true;
    }

    public void ClearProcessAgentEntries()
    {
        DisableAndClearProcessAgents();
    }

    public long DisableAndClearSessionLogActivity()
    {
        HqState? toEmit = null;
        long newGen;
        lock (_lock)
        {
            newGen = ++_sessionLogGeneration;
            _sessionLogAgents.Clear();
            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }

        return newGen;
    }

    public long GetSessionLogGeneration()
    {
        lock (_lock)
        {
            return _sessionLogGeneration;
        }
    }

    public bool UpdateSessionLogActivity(
        IReadOnlyList<AgentSnapshot> agents,
        double ttlSeconds = 20.0,
        long? expectedGeneration = null)
    {
        HqState? toEmit = null;
        lock (_lock)
        {
            if (expectedGeneration.HasValue && expectedGeneration.Value != _sessionLogGeneration)
            {
                return false;
            }

            var now = _timeProvider();
            double clampedTtl = Math.Clamp(double.IsFinite(ttlSeconds) ? ttlSeconds : 20.0, 2.0, 60.0);

            _sessionLogAgents.Clear();
            foreach (var a in agents)
            {
                // Measure freshness strictly from each observation's own timestamp;
                // never renew TTL for unchanged older observations when republishing a batch.
                DateTimeOffset observedAt = a.UpdatedAt ?? a.LastActivityAt ?? now;
                if (observedAt > now.AddSeconds(5))
                {
                    observedAt = now;
                }

                DateTimeOffset expiresAt = observedAt.AddSeconds(clampedTtl);
                if (now > expiresAt)
                {
                    continue;
                }

                _sessionLogAgents[a.Id] = (a, expiresAt);
            }

            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }

        return true;
    }

    public void ClearSessionLogActivity()
    {
        DisableAndClearSessionLogActivity();
    }

    public void UpdateCiBuilds(IReadOnlyList<BuildInfo> builds)
    {
        HqState? toEmit = null;
        lock (_lock)
        {
            // Replace collector builds per repo to avoid stale runs accumulating
            var updatedRepos = builds.Select(b => b.Repo).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet();
            var staleKeys = _collectorBuilds
                .Where(kv => updatedRepos.Contains(kv.Value.Repo))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var k in staleKeys)
            {
                _collectorBuilds.Remove(k);
            }

            foreach (var b in builds)
            {
                _collectorBuilds[b.Id] = b;
            }

            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }
    }

    public long DisableAndClearCiBuilds()
    {
        HqState? toEmit = null;
        long newGen;
        lock (_lock)
        {
            newGen = ++_ciGeneration;
            _collectorBuilds.Clear();
            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }

        return newGen;
    }

    public long GetCiGeneration()
    {
        lock (_lock)
        {
            return _ciGeneration;
        }
    }

    public bool UpdateCiBuildsForRepo(string repo, IReadOnlyList<BuildInfo> builds, long? expectedGeneration = null)
    {
        HqState? toEmit = null;
        lock (_lock)
        {
            if (expectedGeneration.HasValue && expectedGeneration.Value != _ciGeneration)
            {
                return false;
            }

            var staleKeys = _collectorBuilds
                .Where(kv => string.Equals(kv.Value.Repo, repo, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var k in staleKeys)
            {
                _collectorBuilds.Remove(k);
            }

            foreach (var b in builds)
            {
                _collectorBuilds[b.Id] = b;
            }

            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }

        return true;
    }

    public void ClearCiBuildEntries()
    {
        DisableAndClearCiBuilds();
    }

    public void UpdatePushedEvent(PushedEventPayload payload)
    {
        HqState? toEmit = null;
        lock (_lock)
        {
            var now = _timeProvider();
            string? sanitizedMessage = ClampMessage(payload.Message);

            // Special-case "handoff" events: do NOT mutate agent list or create phantom agents!
            if (string.Equals(payload.Type, "handoff", StringComparison.OrdinalIgnoreCase))
            {
                var fromId = payload.FromAgentId ?? payload.AgentId;
                var toId = payload.ToAgentId;
                var seq = Interlocked.Increment(ref _sequenceCounter);
                double ttl = ClampVisualTtl(payload.TtlSeconds, 10.0);
                AddVisualEventLocked(new HqEvent(
                    seq,
                    "handoff",
                    sanitizedMessage ?? ClampMessage($"Handoff from {fromId} to {toId}")!,
                    now,
                    fromId,
                    ttl,
                    toId,
                    payload.FromIsConcreteId,
                    payload.ToIsConcreteId), now);
                toEmit = RecalculateStateLocked();
            }
            else if (string.Equals(payload.Type, "review_changes", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(payload.Type, "drama", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(payload.Type, "request_changes", StringComparison.OrdinalIgnoreCase))
            {
                var fromId = !string.IsNullOrWhiteSpace(payload.FromAgentId)
                    ? payload.FromAgentId
                    : (!string.IsNullOrWhiteSpace(payload.AgentId) ? payload.AgentId : "kakashi");
                var toId = !string.IsNullOrWhiteSpace(payload.ToAgentId) ? payload.ToAgentId : "minato";
                var seq = Interlocked.Increment(ref _sequenceCounter);
                double ttl = ClampVisualTtl(payload.TtlSeconds, 10.0);
                AddVisualEventLocked(new HqEvent(
                    seq,
                    "review_changes",
                    sanitizedMessage ?? ClampMessage($"Review REQUEST_CHANGES from {fromId} to {toId}")!,
                    now,
                    fromId,
                    ttl,
                    toId,
                    payload.FromIsConcreteId,
                    payload.ToIsConcreteId), now);
                toEmit = RecalculateStateLocked();
            }
            else if (string.Equals(payload.Type, "celebrate", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(payload.Type, "celebration", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(payload.Type, "pr_merged", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(payload.Type, "ci_pass", StringComparison.OrdinalIgnoreCase))
            {
                var seq = Interlocked.Increment(ref _sequenceCounter);
                double ttl = ClampVisualTtl(payload.TtlSeconds, 8.0);
                AddVisualEventLocked(new HqEvent(
                    seq,
                    "celebrate",
                    sanitizedMessage ?? "Squad celebration!",
                    now,
                    payload.AgentId,
                    ttl), now);
                toEmit = RecalculateStateLocked();
            }
            else if (string.Equals(payload.Type, "mixi_poke", StringComparison.OrdinalIgnoreCase))
            {
                var seq = Interlocked.Increment(ref _sequenceCounter);
                double ttl = ClampVisualTtl(payload.TtlSeconds, 4.0);
                AddVisualEventLocked(new HqEvent(
                    seq,
                    "mixi_poke",
                    sanitizedMessage ?? "Do Mixi poked!",
                    now,
                    "mixi-chill",
                    ttl), now);
                toEmit = RecalculateStateLocked();
            }
            else
            {
                // 1. Agent event
                if (!string.IsNullOrEmpty(payload.AgentId) || !string.IsNullOrEmpty(payload.Name))
                {
                    var id = payload.AgentId ?? payload.Name!;
                    var rawName = !string.IsNullOrWhiteSpace(payload.Name)
                        ? payload.Name
                        : (!string.IsNullOrWhiteSpace(payload.Provider)
                            ? AgentAppearanceRegistry.FormatProviderDisplayName(payload.Provider)
                            : payload.AgentId);
                    var role = payload.Role ?? AgentRole.External;
                    var name = AgentAppearanceRegistry.SanitizeDisplayName(rawName, id, "bridge", role);

                    // Resolve canonical Orca bot persona only when no explicit role/provider was supplied
                    if (string.IsNullOrWhiteSpace(payload.Provider) &&
                        !payload.Role.HasValue &&
                        AgentAppearanceRegistry.TryMatchStructuredWorkerPrefix(name, out var canonicalRole, out _))
                    {
                        role = canonicalRole;
                    }

                    // Resolve AgentState: explicit State wins, otherwise map from Type
                    AgentState state = payload.State ?? ResolveStateFromType(payload.Type);

                    var ttl = ClampEntityTtl(payload.TtlSeconds, 30.0);
                    var expiresAt = now.AddSeconds(ttl);

                    var spriteKey = role switch
                    {
                        AgentRole.Leader => "shikamaru",
                        AgentRole.Coder => "minato",
                        AgentRole.Tester => "tsunade",
                        AgentRole.Reviewer => "kakashi",
                        _ => "generic"
                    };

                    var snapshot = new AgentSnapshot(
                        Id: id,
                        Name: name,
                        Role: role,
                        SpriteKey: spriteKey,
                        State: state,
                        ToolName: payload.ToolName,
                        LastMessage: sanitizedMessage,
                        LastActivityAt: now,
                        UpdatedAt: now,
                        Source: "bridge"
                    );

                    _pushedAgents[id] = (snapshot, expiresAt);
                }

                // 2. Build event
                if (!string.IsNullOrEmpty(payload.Repo) && !string.IsNullOrEmpty(payload.BuildStatus))
                {
                    var buildId = $"{payload.Repo}:latest";
                    var ttl = ClampEntityTtl(payload.TtlSeconds, 120.0);
                    var expiresAt = now.AddSeconds(ttl);

                    _pushedBuilds[buildId] = (new BuildInfo(
                        Id: buildId,
                        Repo: payload.Repo,
                        WorkflowName: "CI",
                        Branch: "main",
                        Title: sanitizedMessage ?? "Build Event",
                        Status: "completed",
                        Conclusion: payload.BuildStatus,
                        UpdatedAt: now
                    ), expiresAt);

                    // Cap pushed builds at MaxCappedItems
                    if (_pushedBuilds.Count > MaxCappedItems)
                    {
                        var oldest = _pushedBuilds.OrderBy(kv => kv.Value.ExpiresAt).First().Key;
                        _pushedBuilds.Remove(oldest);
                    }
                }

                // 3. Account quota event
                if (!string.IsNullOrEmpty(payload.AccountProvider))
                {
                    var accKey = $"{payload.AccountProvider}:{payload.AccountName ?? "default"}";
                    double ttl = ClampEntityTtl(payload.TtlSeconds, 300.0);
                    var expiresAt = now.AddSeconds(ttl);

                    var accountInfo = new AccountInfo(
                        Provider: payload.AccountProvider,
                        AccountId: payload.AccountName ?? "default",
                        Label: payload.AccountName ?? "Default",
                        IsActive: true,
                        QuotaPercent: payload.QuotaPercent
                    );

                    if (_pushedAccounts.TryGetValue(accKey, out var existingAcc))
                    {
                        _pushedAccounts[accKey] = (existingAcc.Account with
                        {
                            QuotaPercent = payload.QuotaPercent ?? existingAcc.Account.QuotaPercent
                        }, expiresAt);
                    }
                    else
                    {
                        _pushedAccounts[accKey] = (accountInfo, expiresAt);
                    }

                    // Cap pushed accounts at MaxCappedItems
                    if (_pushedAccounts.Count > MaxCappedItems)
                    {
                        var oldest = _pushedAccounts.OrderBy(kv => kv.Value.ExpiresAt).First().Key;
                        _pushedAccounts.Remove(oldest);
                    }
                }

                toEmit = RecalculateStateLocked();
            }
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }
    }

    public static bool IsKnownHookOrAgentEventType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return true;
        return type.Trim().ToLowerInvariant() switch
        {
            "tool_start" or "tool_complete" or "working" or "start" or "starting" or "running" or "in-progress" or "in_progress" or "busy" or "active" or "streaming" => true,
            "tool_done" or "turn_done" or "turn_complete" or "done" or "pass" or "build_pass" or "success" => true,
            "notification" or "approval" or "needs_approval" or "needs-approval" or "needs_you" or "needs-you" or "waiting" or "waiting_input" or "waiting-input" or "waiting_for_input" or "waiting-for-input" or "blocked" => true,
            "fail" or "failed" or "error" or "build_fail" => true,
            "idle" or "stop" or "clear" => true,
            _ => false
        };
    }

    public static AgentState ResolveStateFromType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return AgentState.Working;

        return type.Trim().ToLowerInvariant() switch
        {
            "tool_start" or "tool_complete" or "working" or "start" or "starting" or "running" or "in-progress" or "in_progress" or "busy" or "active" or "streaming" => AgentState.Working,
            "tool_done" or "turn_done" or "turn_complete" or "done" or "pass" or "build_pass" or "success" => AgentState.Done,
            "notification" or "approval" or "needs_approval" or "needs-approval" or "needs_you" or "needs-you" or "waiting" or "waiting_input" or "waiting-input" or "waiting_for_input" or "waiting-for-input" or "blocked" => AgentState.NeedsApproval,
            "fail" or "failed" or "error" or "build_fail" => AgentState.Failed,
            "idle" or "stop" or "clear" => AgentState.Idle,
            _ => AgentState.Working
        };
    }

    public static double ClampVisualTtl(double? ttlSeconds, double defaultSeconds)
    {
        double raw = ttlSeconds.HasValue && double.IsFinite(ttlSeconds.Value) ? ttlSeconds.Value : defaultSeconds;
        return Math.Clamp(raw, MinVisualEventTtlSeconds, MaxVisualEventTtlSeconds);
    }

    public static double ClampEntityTtl(double? ttlSeconds, double defaultSeconds)
    {
        double raw = ttlSeconds.HasValue && double.IsFinite(ttlSeconds.Value) ? ttlSeconds.Value : defaultSeconds;
        return Math.Clamp(raw, MinPushedEntityTtlSeconds, MaxPushedEntityTtlSeconds);
    }

    public static string? ClampMessage(string? message)
    {
        if (message == null) return null;
        if (message.Length <= MaxMessageLength) return message;
        return message.Substring(0, MaxMessageLength);
    }

    public void Reevaluate()
    {
        HqState? toEmit = null;
        lock (_lock)
        {
            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }
    }

    public void AddVisualEvent(
        string type,
        string message,
        string? agentId = null,
        double ttlSeconds = 10.0,
        string? targetAgentId = null,
        bool fromIsConcreteId = false,
        bool toIsConcreteId = false)
    {
        HqState? toEmit = null;
        lock (_lock)
        {
            var seq = Interlocked.Increment(ref _sequenceCounter);
            var now = _timeProvider();
            AddVisualEventLocked(
                new HqEvent(seq, type, ClampMessage(message) ?? string.Empty, now, agentId, ClampVisualTtl(ttlSeconds, 10.0), targetAgentId, fromIsConcreteId, toIsConcreteId),
                now);
            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }
    }

    private void AddVisualEventLocked(HqEvent ev, DateTimeOffset now)
    {
        _visualEvents.RemoveAll(e => (now - e.Timestamp).TotalSeconds > e.TtlSeconds);

        // Coalesce duplicate events of the same type + endpoints if queue is approaching cap
        if (_visualEvents.Count >= MaxVisualEvents)
        {
            int dupIdx = _visualEvents.FindIndex(e =>
                string.Equals(e.Type, ev.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.AgentId, ev.AgentId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.TargetAgentId, ev.TargetAgentId, StringComparison.OrdinalIgnoreCase));
            if (dupIdx >= 0)
            {
                _visualEvents.RemoveAt(dupIdx);
            }
        }

        _visualEvents.Add(ev);

        while (_visualEvents.Count > MaxVisualEvents)
        {
            _visualEvents.RemoveAt(0);
        }
    }

    private HqState? RecalculateStateLocked()
    {
        var now = _timeProvider();

        // 1. Expire old visual events & enforce hard cap
        _visualEvents.RemoveAll(e => (now - e.Timestamp).TotalSeconds > e.TtlSeconds);
        while (_visualEvents.Count > MaxVisualEvents)
        {
            _visualEvents.RemoveAt(0);
        }

        // 2. Expire old pushed agents and pushed builds
        var expiredPushedAgents = _pushedAgents.Where(kv => now > kv.Value.ExpiresAt).Select(kv => kv.Key).ToList();
        foreach (var k in expiredPushedAgents)
        {
            _pushedAgents.Remove(k);
        }

        var expiredPushedBuilds = _pushedBuilds.Where(kv => now > kv.Value.ExpiresAt).Select(kv => kv.Key).ToList();
        foreach (var k in expiredPushedBuilds)
        {
            _pushedBuilds.Remove(k);
        }

        var expiredSessionLogs = _sessionLogAgents.Where(kv => now > kv.Value.ExpiresAt).Select(kv => kv.Key).ToList();
        foreach (var k in expiredSessionLogs)
        {
            _sessionLogAgents.Remove(k);
        }

        // If Demo scenario active, construct demo state
        if (!string.IsNullOrEmpty(_demoScenario))
        {
            return ApplyDemoScenarioLocked(_demoScenario, now);
        }

        // 3. Merge agents: live orca agents + active pushed agents + process agents (with verified/unambiguous session-log correlation)
        var mergedAgents = new Dictionary<string, AgentSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in _orcaAgents.Values)
            mergedAgents[a.Id] = a;

        foreach (var (_, val) in _pushedAgents)
            mergedAgents[val.Agent.Id] = val.Agent;

        var candidateProcesses = new List<AgentSnapshot>(_processAgents.Count);
        foreach (var a in _processAgents.Values)
        {
            if (!mergedAgents.ContainsKey(a.Id))
            {
                candidateProcesses.Add(a);
            }
        }

        var processOverrides = new Dictionary<string, AgentSnapshot>(StringComparer.OrdinalIgnoreCase);
        var matchedSessionLogKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Order session-log observations freshest-first by each record's own event/write timestamp
        var orderedSessionLogs = new List<(string Key, AgentSnapshot Agent)>(_sessionLogAgents.Count);
        foreach (var (slKey, (slAgent, _)) in _sessionLogAgents)
        {
            orderedSessionLogs.Add((slKey, slAgent));
        }
        orderedSessionLogs.Sort((a, b) =>
            (b.Agent.UpdatedAt ?? b.Agent.LastActivityAt ?? DateTimeOffset.MinValue)
                .CompareTo(a.Agent.UpdatedAt ?? a.Agent.LastActivityAt ?? DateTimeOffset.MinValue));

        foreach (var (slKey, slAgent) in orderedSessionLogs)
        {
            AgentSnapshot? verifiedMatch = null;
            foreach (var proc in candidateProcesses)
            {
                if (IsVerifiedSessionCorrelation(proc, slAgent))
                {
                    verifiedMatch = proc;
                    break;
                }
            }

            // Correlate ONLY when provider AND verified (ProcessId, ProcessStartTicks) process identity agree
            // (production SessionLogActivityCollector never emits workspace/repo paths for privacy).
            // NEVER overwrite an unrelated live process!
            if (verifiedMatch != null)
            {
                if (!processOverrides.ContainsKey(verifiedMatch.Id))
                {
                    processOverrides[verifiedMatch.Id] = verifiedMatch with
                    {
                        State = slAgent.State,
                        ToolName = slAgent.ToolName ?? verifiedMatch.ToolName,
                        LastActivityAt = slAgent.LastActivityAt ?? verifiedMatch.LastActivityAt,
                        UpdatedAt = slAgent.UpdatedAt ?? verifiedMatch.UpdatedAt
                    };
                }
                matchedSessionLogKeys.Add(slKey);
            }
        }

        foreach (var proc in candidateProcesses)
        {
            mergedAgents[proc.Id] = processOverrides.TryGetValue(proc.Id, out var enriched) ? enriched : proc;
        }

        // Include fresh uncorrelated session-log observations as ONE separate provider activity tile
        // (picking the freshest uncorrelated log per provider, never mutating unrelated live processes)
        var emittedUncorrelatedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slKey, slAgent) in orderedSessionLogs)
        {
            if (matchedSessionLogKeys.Contains(slKey))
                continue;

            string providerKey = ExtractSessionProvider(slAgent);
            if (!string.IsNullOrWhiteSpace(providerKey) && emittedUncorrelatedProviders.Contains(providerKey))
                continue;

            bool alreadyRepresentedByAuthoritative = false;
            foreach (var existing in mergedAgents.Values)
            {
                if (string.Equals(existing.Id, slAgent.Id, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyRepresentedByAuthoritative = true;
                    break;
                }

                if (!string.Equals(existing.Source, "process", StringComparison.OrdinalIgnoreCase) &&
                    MatchesSessionProvider(existing, slAgent))
                {
                    alreadyRepresentedByAuthoritative = true;
                    break;
                }
            }

            if (!alreadyRepresentedByAuthoritative)
            {
                mergedAgents[slAgent.Id] = slAgent;
                if (!string.IsNullOrWhiteSpace(providerKey))
                {
                    emittedUncorrelatedProviders.Add(providerKey);
                }
            }
        }

        // 4. Celebration window & relaxation / pruning
        var finalAgents = new List<AgentSnapshot>(mergedAgents.Count + _departedDoneAgents.Count);
        var celebrationSeconds = _config.CelebrationDurationSeconds;

        foreach (var a in mergedAgents.Values)
        {
            if (a.State == AgentState.Done)
            {
                if (!_doneStateEnteredAt.TryGetValue(a.Id, out var enteredAt))
                {
                    enteredAt = a.UpdatedAt ?? now;
                    _doneStateEnteredAt[a.Id] = enteredAt;
                }

                if ((now - enteredAt).TotalSeconds > celebrationSeconds)
                {
                    // Live agent celebration expired: relax to Idle and clear stale tool/work timer
                    finalAgents.Add(a with { State = AgentState.Idle, ToolName = null, WorkStartedAt = null });
                }
                else
                {
                    finalAgents.Add(a);
                }
            }
            else
            {
                _doneStateEnteredAt.Remove(a.Id);
                finalAgents.Add(a);
            }
        }

        // Handle departed Done agents (no longer in live mergedAgents)
        var expiredDeparted = new List<string>();
        foreach (var (depId, (depAgent, enteredAt)) in _departedDoneAgents)
        {
            if (mergedAgents.ContainsKey(depId))
            {
                expiredDeparted.Add(depId);
                continue;
            }

            if ((now - enteredAt).TotalSeconds <= celebrationSeconds)
            {
                // Still in celebration window
                finalAgents.Add(depAgent);
            }
            else
            {
                // Celebration expired: PRUNE departed agent completely!
                expiredDeparted.Add(depId);
            }
        }

        foreach (var exp in expiredDeparted)
        {
            _departedDoneAgents.Remove(exp);
            _doneStateEnteredAt.Remove(exp);
        }

        // 4b. Track WorkStartedAt for agents
        for (int i = 0; i < finalAgents.Count; i++)
        {
            var a = finalAgents[i];
            if (a.State == AgentState.Working || a.State == AgentState.NeedsApproval)
            {
                if (a.WorkStartedAt.HasValue)
                {
                    _workStartedAt[a.Id] = a.WorkStartedAt.Value;
                }
                else
                {
                    if (_workStartedAt.TryGetValue(a.Id, out var existingStart))
                    {
                        finalAgents[i] = a with { WorkStartedAt = existingStart };
                    }
                    else
                    {
                        _workStartedAt[a.Id] = now;
                        finalAgents[i] = a with { WorkStartedAt = now };
                    }
                }
            }
            else
            {
                // Agent transitioned out of Working / NeedsApproval (e.g. Idle, Done, Failed)
                _workStartedAt.Remove(a.Id);
                _lastWorkingStartedAt.Remove(a.Id);
                if (a.WorkStartedAt.HasValue)
                {
                    finalAgents[i] = a with { WorkStartedAt = null };
                }
            }
        }

        // 4c. Apply clean names, Orca-fixed vs non-Orca randomized character variety & desk rigs
        _appearanceRegistry.ApplyToAgents(finalAgents);

        var liveIds = finalAgents.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staleWorkKeys = _workStartedAt.Keys.Where(k => !liveIds.Contains(k)).ToList();
        foreach (var k in staleWorkKeys)
        {
            _workStartedAt.Remove(k);
        }
        var staleWorkingStarted = _lastWorkingStartedAt.Keys.Where(k => !liveIds.Contains(k)).ToList();
        foreach (var k in staleWorkingStarted)
        {
            _lastWorkingStartedAt.Remove(k);
        }
        var staleDoneEntered = _doneStateEnteredAt.Keys.Where(k => !liveIds.Contains(k) && !_departedDoneAgents.ContainsKey(k)).ToList();
        foreach (var k in staleDoneEntered)
        {
            _doneStateEnteredAt.Remove(k);
        }

        // 5. Detect transitions for visual events
        var allBuildsMap = new Dictionary<string, BuildInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in _collectorBuilds) allBuildsMap[k] = v;
        foreach (var (k, v) in _pushedBuilds) allBuildsMap[k] = v.Build;
        var buildsList = allBuildsMap.Values.ToList();

        DetectTransitions(finalAgents, buildsList, now);

        var mergedAccounts = GetMergedAccountsLocked(now);

        var weather = ComputeEffectiveWeather(_config.WeatherMode, buildsList, now);
        var mood = ComputeHqMood(finalAgents, buildsList, _visualEvents);

        var newState = new HqState(
            Agents: finalAgents.OrderBy(a => a.Role).ThenBy(a => a.Name).ToList(),
            Accounts: mergedAccounts,
            Builds: buildsList.OrderByDescending(b => b.CreatedAt ?? b.UpdatedAt ?? DateTimeOffset.MinValue).ToList(),
            Weather: weather,
            Mood: mood,
            PendingVisualEvents: _visualEvents.OrderBy(e => e.SequenceId).ToArray(),
            DemoScenario: null,
            Timestamp: now
        );

        if (newState.Equals(_currentState))
        {
            return null; // Suppress no-op
        }

        _revisionCounter++;
        _currentState = newState with { Revision = _revisionCounter };
        return _currentState;
    }

    private void DetectTransitions(IReadOnlyList<AgentSnapshot> currentAgents, IReadOnlyList<BuildInfo> currentBuilds, DateTimeOffset now)
    {
        var prevAgents = _currentState.Agents;
        var prevBuilds = _currentState.Builds;

        // Clone transitions
        var prevClones = prevAgents.Where(a => a.Role == AgentRole.Clone).ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var currClones = currentAgents.Where(a => a.Role == AgentRole.Clone).ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var (id, clone) in currClones)
        {
            if (!prevClones.ContainsKey(id))
            {
                var seq = Interlocked.Increment(ref _sequenceCounter);
                AddVisualEventLocked(new HqEvent(seq, "CloneSpawned", $"{clone.Name} joined", now, id, 8.0), now);
            }
        }

        foreach (var (id, clone) in prevClones)
        {
            if (!currClones.ContainsKey(id))
            {
                var seq = Interlocked.Increment(ref _sequenceCounter);
                AddVisualEventLocked(new HqEvent(seq, "CloneDespawned", $"{clone.Name} exited", now, id, 8.0), now);
            }
        }

        // Approval raised, Done celebration, & Review REQUEST_CHANGES drama detection
        var prevAgentMap = prevAgents.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        bool anyDoneTransition = false;
        foreach (var ag in currentAgents)
        {
            prevAgentMap.TryGetValue(ag.Id, out var prev);

            if (ag.State == AgentState.NeedsApproval)
            {
                var wasNeedsApproval = prev != null && prev.State == AgentState.NeedsApproval;
                if (!wasNeedsApproval)
                {
                    var seq = Interlocked.Increment(ref _sequenceCounter);
                    AddVisualEventLocked(new HqEvent(seq, "ApprovalRaised", ClampMessage($"{ag.Name} needs approval: {ag.QuestionPrompt ?? "Pending question"}")!, now, ag.Id, 15.0), now);
                }
            }

            if (ag.State == AgentState.Done && (prev == null || prev.State != AgentState.Done))
            {
                anyDoneTransition = true;
            }

            // Detect Orca/hook Reviewer issuing REQUEST_CHANGES -> trigger Kakashi->Minato drama
            bool isReviewer = ag.Role == AgentRole.Reviewer ||
                              string.Equals(ag.SpriteKey, "kakashi", StringComparison.OrdinalIgnoreCase);
            if (isReviewer && HasReviewRejectMarker(ag) && (prev == null || !HasReviewRejectMarker(prev)))
            {
                var coder = currentAgents.FirstOrDefault(a =>
                    a.Role == AgentRole.Coder ||
                    string.Equals(a.SpriteKey, "minato", StringComparison.OrdinalIgnoreCase));
                string toId = coder?.Id ?? "minato";
                bool toConcrete = coder != null;
                var seq = Interlocked.Increment(ref _sequenceCounter);
                AddVisualEventLocked(new HqEvent(
                    seq,
                    "review_changes",
                    $"{ag.Name} requested changes from {coder?.Name ?? "Minato"}",
                    now,
                    ag.Id,
                    10.0,
                    toId,
                    FromIsConcreteId: true,
                    ToIsConcreteId: toConcrete), now);
            }
        }

        if (anyDoneTransition)
        {
            var seq = Interlocked.Increment(ref _sequenceCounter);
            AddVisualEventLocked(new HqEvent(seq, "celebrate", "Task completed! Squad celebration!", now, null, 6.0), now);
        }

        // Build pass / fail transitions
        var prevBuildMap = prevBuilds.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var b in currentBuilds)
        {
            var hadPrev = prevBuildMap.TryGetValue(b.Id, out var prevB);

            if (b.IsSuccessful && (!hadPrev || !prevB!.IsSuccessful))
            {
                var seq = Interlocked.Increment(ref _sequenceCounter);
                AddVisualEventLocked(new HqEvent(seq, "BuildPassed", $"Build {b.Repo} passed", now, null, 10.0), now);
            }
            else if (b.IsFailing && (!hadPrev || !prevB!.IsFailing))
            {
                var seq = Interlocked.Increment(ref _sequenceCounter);
                AddVisualEventLocked(new HqEvent(seq, "BuildFailed", $"Build {b.Repo} failed", now, null, 12.0), now);
            }
        }

        // Handoff transitions: Agent A exits Working (Done/Idle/etc) within 15s of Agent B entering Working
        var staleFinished = _lastWorkingFinishedAt.Where(kv => (now - kv.Value).TotalSeconds > 15.0).Select(kv => kv.Key).ToList();
        foreach (var k in staleFinished) _lastWorkingFinishedAt.Remove(k);
        var staleStarted = _lastWorkingStartedAt.Where(kv => (now - kv.Value).TotalSeconds > 15.0).Select(kv => kv.Key).ToList();
        foreach (var k in staleStarted) _lastWorkingStartedAt.Remove(k);

        // Detect agents exiting Working
        foreach (var (prevId, prevAg) in prevAgentMap)
        {
            if (prevAg.State == AgentState.Working)
            {
                var currAg = currentAgents.FirstOrDefault(a => string.Equals(a.Id, prevId, StringComparison.OrdinalIgnoreCase));
                if (currAg == null || currAg.State != AgentState.Working)
                {
                    _lastWorkingFinishedAt[prevId] = now;

                    var matchB = _lastWorkingStartedAt
                        .Where(kv => !string.Equals(kv.Key, prevId, StringComparison.OrdinalIgnoreCase) && (now - kv.Value).TotalSeconds <= 15.0)
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => kv.Key)
                        .FirstOrDefault();

                    if (matchB != null)
                    {
                        _lastWorkingStartedAt.Remove(matchB);
                        _lastWorkingFinishedAt.Remove(prevId);
                        var seq = Interlocked.Increment(ref _sequenceCounter);
                        AddVisualEventLocked(new HqEvent(seq, "handoff", $"Handoff from {prevAg.Name} to {matchB}", now, prevId, 10.0, matchB, FromIsConcreteId: true, ToIsConcreteId: true), now);
                    }
                }
            }
        }

        // Detect agents entering Working
        foreach (var currAg in currentAgents)
        {
            if (currAg.State == AgentState.Working)
            {
                var hadPrevWorking = prevAgentMap.TryGetValue(currAg.Id, out var prevAg) && prevAg.State == AgentState.Working;
                if (!hadPrevWorking)
                {
                    _lastWorkingStartedAt[currAg.Id] = now;

                    var matchA = _lastWorkingFinishedAt
                        .Where(kv => !string.Equals(kv.Key, currAg.Id, StringComparison.OrdinalIgnoreCase) && (now - kv.Value).TotalSeconds <= 15.0)
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => kv.Key)
                        .FirstOrDefault();

                    if (matchA != null)
                    {
                        _lastWorkingFinishedAt.Remove(matchA);
                        _lastWorkingStartedAt.Remove(currAg.Id);
                        var seq = Interlocked.Increment(ref _sequenceCounter);
                        AddVisualEventLocked(new HqEvent(seq, "handoff", $"Handoff from {matchA} to {currAg.Name}", now, matchA, 10.0, currAg.Id, FromIsConcreteId: true, ToIsConcreteId: true), now);
                    }
                }
            }
        }
    }

    private static bool IsVerifiedSessionCorrelation(AgentSnapshot candidate, AgentSnapshot sessionAgent)
    {
        // ALWAYS verify provider match first — NEVER correlate across different providers even on reused PID!
        if (!MatchesSessionProvider(candidate, sessionAgent))
        {
            return false;
        }

        bool sessionHasPid = sessionAgent.ProcessId.HasValue && sessionAgent.ProcessId.Value > 0;
        bool sessionHasStartTicks = sessionAgent.ProcessStartTicks.HasValue && sessionAgent.ProcessStartTicks.Value > 0;
        bool candidateHasPid = candidate.ProcessId.HasValue && candidate.ProcessId.Value > 0;
        bool candidateHasStartTicks = candidate.ProcessStartTicks.HasValue && candidate.ProcessStartTicks.Value > 0;

        // Verified (Provider, ProcessId, ProcessStartTicks) tuple check.
        // Whenever sessionAgent carries a PID, require verified start-generation ticks on both sides
        // (NEVER guess by bare PID or fall through to weaker fields).
        if (sessionHasPid)
        {
            return sessionHasStartTicks &&
                   candidateHasPid &&
                   candidateHasStartTicks &&
                   sessionAgent.ProcessId!.Value == candidate.ProcessId!.Value &&
                   sessionAgent.ProcessStartTicks!.Value == candidate.ProcessStartTicks!.Value;
        }

        // Production SessionLogActivityCollector never emits WorktreeId or Repo (privacy: zero paths).
        // Only caller-injected snapshots without a PID that explicitly provide matching WorktreeId/Repo can match here.
        if (!string.IsNullOrWhiteSpace(sessionAgent.WorktreeId) &&
            !string.IsNullOrWhiteSpace(candidate.WorktreeId) &&
            string.Equals(sessionAgent.WorktreeId, candidate.WorktreeId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(sessionAgent.Repo) &&
            !string.IsNullOrWhiteSpace(candidate.Repo) &&
            string.Equals(sessionAgent.Repo, candidate.Repo, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string ExtractSessionProvider(AgentSnapshot sessionAgent)
    {
        if (!string.IsNullOrWhiteSpace(sessionAgent.Id) &&
            sessionAgent.Id.StartsWith("session:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = sessionAgent.Id.Substring("session:".Length);
            int colon = rest.IndexOf(':');
            return colon > 0 ? rest.Substring(0, colon) : rest;
        }

        return sessionAgent.Name ?? string.Empty;
    }

    private static bool MatchesSessionProvider(AgentSnapshot candidate, AgentSnapshot sessionAgent)
    {
        string provider = ExtractSessionProvider(sessionAgent);
        if (string.IsNullOrWhiteSpace(provider)) return false;

        string name = candidate.Name ?? string.Empty;
        if (name.Equals(provider, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(provider + " ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(candidate.SpriteKey) &&
            candidate.SpriteKey.Equals(provider, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Id) &&
            candidate.Id.Contains(provider, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool HasReviewRejectMarker(AgentSnapshot agent)
    {
        return ContainsRejectToken(agent.LastMessage) || ContainsRejectToken(agent.TaskTitle);
    }

    private static bool ContainsRejectToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("REQUEST_CHANGES", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("REQUEST CHANGES", StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<AccountInfo> GetMergedAccountsLocked(DateTimeOffset now)
    {
        var expiredAccounts = _pushedAccounts.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList();
        foreach (var k in expiredAccounts)
        {
            _pushedAccounts.Remove(k);
        }

        var mergedAccounts = new Dictionary<string, AccountInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in _orcaAccounts)
        {
            mergedAccounts[k] = v;
        }
        foreach (var (k, (acc, _)) in _pushedAccounts)
        {
            if (mergedAccounts.TryGetValue(k, out var existingOrca))
            {
                mergedAccounts[k] = existingOrca with
                {
                    QuotaPercent = acc.QuotaPercent ?? existingOrca.QuotaPercent
                };
            }
            else
            {
                mergedAccounts[k] = acc;
            }
        }

        return mergedAccounts.Values.OrderBy(a => a.Provider).ThenBy(a => a.AccountId).ToList();
    }

    private HqState? ApplyDemoScenarioLocked(string scenario, DateTimeOffset now)
    {
        var agents = new List<AgentSnapshot>();
        var builds = new List<BuildInfo>();

        switch (scenario)
        {
            case "working":
                var workAnchor = _demoAnchorTime == default ? now : _demoAnchorTime;
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru (Team Leader)", AgentRole.Leader, "shikamaru", AgentState.Working, ToolName: "execute", TaskTitle: "Refactoring state engine", IsBotHome: true, WorkStartedAt: workAnchor.AddSeconds(-60)));
                agents.Add(new AgentSnapshot("demo-coder", "Minato (Senior Coder)", AgentRole.Coder, "minato", AgentState.Working, ToolName: "compile", TaskTitle: "Writing unit tests", IsBotHome: true, WorkStartedAt: workAnchor.AddSeconds(-120)));
                break;

            case "approve":
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru (Team Leader)", AgentRole.Leader, "shikamaru", AgentState.NeedsApproval, QuestionPrompt: "Approve deployment to production?", TaskTitle: "Deploy gate", IsBotHome: true, Unread: true));
                break;

            case "done":
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru (Team Leader)", AgentRole.Leader, "shikamaru", AgentState.Done, TaskTitle: "Milestone complete", UpdatedAt: now, IsBotHome: true));
                break;

            case "fail":
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru (Team Leader)", AgentRole.Leader, "shikamaru", AgentState.Failed, LastMessage: "Exception: Build broken", IsBotHome: true));
                builds.Add(new BuildInfo("demo-build", "kagedesk", "CI", "main", "Build #42", "completed", "failure", UpdatedAt: now));
                break;

            case "clone":
                var cloneAnchor = _demoAnchorTime == default ? now : _demoAnchorTime;
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru (Team Leader)", AgentRole.Leader, "shikamaru", AgentState.Working, ToolName: "execute", IsBotHome: true, Source: "orca", WorkStartedAt: cloneAnchor.AddSeconds(-40)));
                agents.Add(new AgentSnapshot("demo-term-1", "OMP", AgentRole.External, "dj-synth", AgentState.Working, ToolName: "edit", Source: "process", WorkStartedAt: cloneAnchor.AddSeconds(-30), DeskRig: DeskRigKind.RgbStreamerRig, BehaviorVariant: 0));
                agents.Add(new AgentSnapshot("demo-term-2", "Claude", AgentRole.External, "neko-dev", AgentState.Working, ToolName: "test", Source: "process", WorkStartedAt: cloneAnchor.AddSeconds(-20), DeskRig: DeskRigKind.CozyLofiRig, BehaviorVariant: 1));
                agents.Add(new AgentSnapshot("demo-term-3", "Codex", AgentRole.External, "cyber-hacker", AgentState.Working, ToolName: "build", Source: "process", WorkStartedAt: cloneAnchor.AddSeconds(-15), DeskRig: DeskRigKind.CyberConsoleRig, BehaviorVariant: 2));
                agents.Add(new AgentSnapshot("demo-term-4", "AGY", AgentRole.External, "ronin-dev", AgentState.Working, ToolName: "search", Source: "process", WorkStartedAt: cloneAnchor.AddSeconds(-10), DeskRig: DeskRigKind.RetroCrtRig, BehaviorVariant: 0));
                break;

            case "variety":
                var varAnchor = _demoAnchorTime == default ? now : _demoAnchorTime;
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru (Team Leader)", AgentRole.Leader, "shikamaru", AgentState.Idle, IsBotHome: true, Source: "orca", BehaviorVariant: 0));
                agents.Add(new AgentSnapshot("demo-coder", "Minato (Senior Coder)", AgentRole.Coder, "minato", AgentState.Working, ToolName: "edit", TaskTitle: "Core engine", IsBotHome: true, Source: "orca", WorkStartedAt: varAnchor.AddSeconds(-75), BehaviorVariant: 1));
                agents.Add(new AgentSnapshot("demo-tester", "Tsunade (QA Lead)", AgentRole.Tester, "tsunade", AgentState.Idle, IsBotHome: true, Source: "orca", BehaviorVariant: 2));
                agents.Add(new AgentSnapshot("demo-reviewer", "Kakashi (Code Reviewer)", AgentRole.Reviewer, "kakashi", AgentState.Idle, IsBotHome: true, Source: "orca", BehaviorVariant: 0));
                agents.Add(new AgentSnapshot("demo-var-1", "OMP", AgentRole.Coder, "dj-synth", AgentState.Working, ToolName: "edit", TaskTitle: "Synthwave shader pass", Source: "process", WorkStartedAt: varAnchor.AddSeconds(-55), DeskRig: DeskRigKind.RgbStreamerRig, BehaviorVariant: 0));
                agents.Add(new AgentSnapshot("demo-var-2", "OMP 2", AgentRole.Coder, "neko-dev", AgentState.Working, ToolName: "thinking", TaskTitle: "Lo-fi shader tuning", Source: "process", WorkStartedAt: varAnchor.AddSeconds(-35), DeskRig: DeskRigKind.CozyLofiRig, BehaviorVariant: 1));
                agents.Add(new AgentSnapshot("demo-var-3", "Claude", AgentRole.Coder, "cyber-hacker", AgentState.Working, ToolName: "exec", TaskTitle: "Running benchmarks", Source: "process", WorkStartedAt: varAnchor.AddSeconds(-25), DeskRig: DeskRigKind.CyberConsoleRig, BehaviorVariant: 2));
                agents.Add(new AgentSnapshot("demo-var-4", "Codex", AgentRole.Coder, "astro-dev", AgentState.Working, ToolName: "git", TaskTitle: "Branch sync", Source: "process", WorkStartedAt: varAnchor.AddSeconds(-15), DeskRig: DeskRigKind.RetroCrtRig, BehaviorVariant: 1));
                agents.Add(new AgentSnapshot("demo-var-5", "AGY", AgentRole.External, "mecha-droid", AgentState.Idle, TaskTitle: "Standby console", Source: "process", DeskRig: DeskRigKind.DualMonitorRig, BehaviorVariant: 2));
                break;

            case "mixi":
                // Exclusive All-Idle Do Mixi Gaming Chill Mode demo (all shinobi off-duty/idle)
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru (Team Leader)", AgentRole.Leader, "shikamaru", AgentState.Idle, IsBotHome: true, Source: "orca", BehaviorVariant: 0));
                agents.Add(new AgentSnapshot("demo-coder", "Minato (Senior Coder)", AgentRole.Coder, "minato", AgentState.Idle, IsBotHome: true, Source: "orca", BehaviorVariant: 1));
                agents.Add(new AgentSnapshot("demo-tester", "Tsunade (QA Lead)", AgentRole.Tester, "tsunade", AgentState.Idle, IsBotHome: true, Source: "orca", BehaviorVariant: 2));
                agents.Add(new AgentSnapshot("demo-reviewer", "Kakashi (Code Reviewer)", AgentRole.Reviewer, "kakashi", AgentState.Idle, IsBotHome: true, Source: "orca", BehaviorVariant: 0));
                break;

            case "handoff":
                var anchor = _demoAnchorTime == default ? now : _demoAnchorTime;
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru (Team Leader)", AgentRole.Leader, "shikamaru", AgentState.Idle, IsBotHome: true));
                agents.Add(new AgentSnapshot("demo-coder", "Minato (Senior Coder)", AgentRole.Coder, "minato", AgentState.Working, ToolName: "compile", TaskTitle: "Refactoring state engine", IsBotHome: true, WorkStartedAt: anchor.AddSeconds(-45)));
                agents.Add(new AgentSnapshot("demo-tester", "Tsunade (QA Lead)", AgentRole.Tester, "tsunade", AgentState.Working, ToolName: "test", TaskTitle: "Integration testing", IsBotHome: true, WorkStartedAt: anchor.AddSeconds(-10)));
                agents.Add(new AgentSnapshot("demo-reviewer", "Kakashi (Code Reviewer)", AgentRole.Reviewer, "kakashi", AgentState.Idle, IsBotHome: true));
                if (!_demoHandoffEmitted)
                {
                    _demoHandoffEmitted = true;
                    var handoffSeq = Interlocked.Increment(ref _sequenceCounter);
                    _visualEvents.Add(new HqEvent(handoffSeq, "handoff", "Scroll handoff from Minato to Tsunade", anchor, "demo-coder", 10.0, "demo-tester"));
                }
                break;

            case "drama":
                var dramaAnchor = _demoAnchorTime == default ? now : _demoAnchorTime;
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru (Team Leader)", AgentRole.Leader, "shikamaru", AgentState.Idle, IsBotHome: true, Source: "orca"));
                agents.Add(new AgentSnapshot("demo-coder", "Minato (Senior Coder)", AgentRole.Coder, "minato", AgentState.Working, ToolName: "code", TaskTitle: "Fixing review feedback", IsBotHome: true, Source: "orca", WorkStartedAt: dramaAnchor.AddSeconds(-30)));
                agents.Add(new AgentSnapshot("demo-tester", "Tsunade (QA Lead)", AgentRole.Tester, "tsunade", AgentState.Idle, IsBotHome: true, Source: "orca"));
                agents.Add(new AgentSnapshot("demo-reviewer", "Kakashi (Code Reviewer)", AgentRole.Reviewer, "kakashi", AgentState.Working, ToolName: "review", TaskTitle: "REQUEST_CHANGES on PR #22", IsBotHome: true, Source: "orca", WorkStartedAt: dramaAnchor.AddSeconds(-10)));
                if (!_demoDramaEmitted)
                {
                    _demoDramaEmitted = true;
                    var dramaSeq = Interlocked.Increment(ref _sequenceCounter);
                    _visualEvents.Add(new HqEvent(dramaSeq, "review_changes", "Kakashi threw REQUEST_CHANGES scroll to Minato!", dramaAnchor, "demo-reviewer", 10.0, "demo-coder", FromIsConcreteId: true, ToIsConcreteId: true));
                }
                break;

            case "celebrate":
                var celAnchor = _demoAnchorTime == default ? now : _demoAnchorTime;
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru (Team Leader)", AgentRole.Leader, "shikamaru", AgentState.Done, TaskTitle: "PR merged!", UpdatedAt: celAnchor, IsBotHome: true, Source: "orca"));
                agents.Add(new AgentSnapshot("demo-coder", "Minato (Senior Coder)", AgentRole.Coder, "minato", AgentState.Done, TaskTitle: "CI passed!", UpdatedAt: celAnchor, IsBotHome: true, Source: "orca"));
                agents.Add(new AgentSnapshot("demo-tester", "Tsunade (QA Lead)", AgentRole.Tester, "tsunade", AgentState.Done, TaskTitle: "All tests green!", UpdatedAt: celAnchor, IsBotHome: true, Source: "orca"));
                agents.Add(new AgentSnapshot("demo-reviewer", "Kakashi (Code Reviewer)", AgentRole.Reviewer, "kakashi", AgentState.Done, TaskTitle: "LGTM approved!", UpdatedAt: celAnchor, IsBotHome: true, Source: "orca"));
                builds.Add(new BuildInfo("demo-pass", "kagedesk", "CI", "main", "Release v2.2", "completed", "success", UpdatedAt: celAnchor));
                if (!_demoCelebrateEmitted)
                {
                    _demoCelebrateEmitted = true;
                    var celSeq = Interlocked.Increment(ref _sequenceCounter);
                    _visualEvents.Add(new HqEvent(celSeq, "celebrate", "Squad celebration! CI green & PR merged!", celAnchor, null, 8.0));
                }
                break;
        }

        var weather = ComputeEffectiveWeather(_config.WeatherMode, builds, now);
        var mood = ComputeHqMood(agents, builds, _visualEvents);

        var demoState = new HqState(
            Agents: agents,
            Accounts: GetMergedAccountsLocked(now),
            Builds: builds,
            Weather: weather,
            Mood: mood,
            PendingVisualEvents: _visualEvents.ToArray(),
            DemoScenario: scenario,
            Timestamp: now
        );

        if (demoState.Equals(_currentState))
        {
            return null;
        }

        _revisionCounter++;
        _currentState = demoState with { Revision = _revisionCounter };
        return _currentState;
    }

    public static EffectiveWeather ComputeEffectiveWeather(WeatherMode mode, IReadOnlyList<BuildInfo> builds, DateTimeOffset now)
    {
        // Fresh CI storm logic: compute failure based ONLY on latest build per repo
        var latestPerRepo = builds
            .GroupBy(b => b.Repo, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(b => b.CreatedAt ?? b.UpdatedAt ?? DateTimeOffset.MinValue).First())
            .ToList();

        bool anyLatestFailing = latestPerRepo.Any(b => b.IsFailing);

        if (mode == WeatherMode.Auto)
        {
            if (anyLatestFailing)
            {
                return EffectiveWeather.StormAlert;
            }

            var localHour = now.ToLocalTime().Hour;
            if (localHour >= 5 && localHour <= 8)
                return EffectiveWeather.Dawn;
            if (localHour >= 9 && localHour <= 16)
                return EffectiveWeather.Day;
            if (localHour >= 17 && localHour <= 19)
                return EffectiveWeather.Sunset;

            return EffectiveWeather.MidnightRain; // 20..04
        }

        // Forced weather mode
        return mode switch
        {
            WeatherMode.Dawn => EffectiveWeather.Dawn,
            WeatherMode.Day => EffectiveWeather.Day,
            WeatherMode.Sunset => EffectiveWeather.Sunset,
            WeatherMode.MidnightRain => EffectiveWeather.MidnightRain,
            WeatherMode.StormAlert => EffectiveWeather.StormAlert,
            _ => EffectiveWeather.Day
        };
    }

    public static HqMood ComputeHqMood(
        IReadOnlyList<AgentSnapshot> agents,
        IReadOnlyList<BuildInfo> builds,
        IReadOnlyList<HqEvent> visualEvents)
    {
        // 1. Alert (Approval)
        if (agents.Any(a => a.State == AgentState.NeedsApproval) ||
            visualEvents.Any(e => string.Equals(e.Type, "ApprovalRaised", StringComparison.OrdinalIgnoreCase)))
        {
            return HqMood.Alert;
        }

        // 2. Failing (Build fail or agent fail) - check latest build per repo
        var latestPerRepo = builds
            .GroupBy(b => b.Repo, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(b => b.CreatedAt ?? b.UpdatedAt ?? DateTimeOffset.MinValue).First())
            .ToList();

        if (latestPerRepo.Any(b => b.IsFailing) ||
            agents.Any(a => a.State == AgentState.Failed) ||
            visualEvents.Any(e => string.Equals(e.Type, "BuildFailed", StringComparison.OrdinalIgnoreCase)))
        {
            return HqMood.Failing;
        }

        // 3. Busy (Working)
        if (agents.Any(a => a.State == AgentState.Working))
        {
            return HqMood.Busy;
        }

        // 4. Celebrating (Done, BuildPassed, or celebrate)
        if (agents.Any(a => a.State == AgentState.Done) ||
            visualEvents.Any(e => string.Equals(e.Type, "BuildPassed", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(e.Type, "celebrate", StringComparison.OrdinalIgnoreCase)))
        {
            return HqMood.Celebrating;
        }

        // 5. Calm (Idle / default)
        return HqMood.Calm;
    }
}
