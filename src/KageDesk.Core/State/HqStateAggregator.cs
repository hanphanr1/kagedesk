using System.Collections.Concurrent;
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
    double? TtlSeconds = null
);

public class HqStateAggregator
{
    private const int MaxCappedItems = 32;

    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _timeProvider;
    private readonly AppConfig _config;

    private long _sequenceCounter;

    private readonly Dictionary<string, AgentSnapshot> _orcaAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (AgentSnapshot Agent, DateTimeOffset ExpiresAt)> _pushedAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AgentSnapshot> _processAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AccountInfo> _orcaAccounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (AccountInfo Account, DateTimeOffset ExpiresAt)> _pushedAccounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (BuildInfo Build, DateTimeOffset ExpiresAt)> _pushedBuilds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BuildInfo> _collectorBuilds = new(StringComparer.OrdinalIgnoreCase);
    private long _ciGeneration;

    private readonly List<HqEvent> _visualEvents = new();
    private readonly Dictionary<string, (AgentSnapshot LastSnapshot, DateTimeOffset EnteredAt)> _departedDoneAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _doneStateEnteredAt = new(StringComparer.OrdinalIgnoreCase);

    private string? _demoScenario;
    private HqState _currentState;

    public event EventHandler<HqState>? StateChanged;

    public HqStateAggregator(AppConfig? config = null, Func<DateTimeOffset>? timeProvider = null)
    {
        _config = config ?? new AppConfig();
        _timeProvider = timeProvider ?? (() => DateTimeOffset.UtcNow);

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
            }
            else
            {
                var lower = scenario.Trim().ToLowerInvariant();
                if (lower is not ("working" or "approve" or "done" or "fail" or "clone"))
                {
                    throw new ArgumentException($"Unknown demo scenario: '{scenario}'", nameof(scenario));
                }
                _demoScenario = lower;
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

    public void UpdateProcessAgents(IReadOnlyList<AgentSnapshot> agents)
    {
        HqState? toEmit = null;
        lock (_lock)
        {
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
    }

    public void ClearProcessAgentEntries()
    {
        HqState? toEmit = null;
        lock (_lock)
        {
            _processAgents.Clear();
            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }
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

            // 1. Agent event
            if (!string.IsNullOrEmpty(payload.AgentId) || !string.IsNullOrEmpty(payload.Name))
            {
                var id = payload.AgentId ?? payload.Name!;
                var name = payload.Name ?? payload.AgentId!;
                var role = payload.Role ?? AgentRole.External;

                // Resolve AgentState: explicit State wins, otherwise map from Type
                AgentState state = payload.State ?? ResolveStateFromType(payload.Type);

                var ttl = payload.TtlSeconds ?? 30.0;
                var expiresAt = now.AddSeconds(ttl);

                var spriteKey = role switch
                {
                    AgentRole.Leader => "shikamaru",
                    AgentRole.Coder => "minato",
                    AgentRole.Tester => "tsunade",
                    AgentRole.Reviewer => "kakashi",
                    AgentRole.Clone => "clone",
                    _ => "generic"
                };

                var snapshot = new AgentSnapshot(
                    Id: id,
                    Name: name,
                    Role: role,
                    SpriteKey: spriteKey,
                    State: state,
                    ToolName: payload.ToolName,
                    LastMessage: payload.Message,
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
                var ttl = payload.TtlSeconds ?? 120.0;
                var expiresAt = now.AddSeconds(ttl);

                _pushedBuilds[buildId] = (new BuildInfo(
                    Id: buildId,
                    Repo: payload.Repo,
                    WorkflowName: "CI",
                    Branch: "main",
                    Title: payload.Message ?? "Build Event",
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
                double ttl = payload.TtlSeconds ?? 300.0;
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

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }
    }

    public static AgentState ResolveStateFromType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return AgentState.Working;

        return type.Trim().ToLowerInvariant() switch
        {
            "tool_start" or "working" or "start" or "running" => AgentState.Working,
            "tool_done" or "done" or "pass" or "build_pass" or "success" => AgentState.Done,
            "notification" or "approval" or "needs_approval" or "waiting_input" => AgentState.NeedsApproval,
            "fail" or "failed" or "error" or "build_fail" => AgentState.Failed,
            "idle" or "stop" or "clear" => AgentState.Idle,
            _ => AgentState.Working
        };
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

    public void AddVisualEvent(string type, string message, string? agentId = null, double ttlSeconds = 10.0)
    {
        HqState? toEmit = null;
        lock (_lock)
        {
            var seq = Interlocked.Increment(ref _sequenceCounter);
            var now = _timeProvider();
            _visualEvents.Add(new HqEvent(seq, type, message, now, agentId, ttlSeconds));
            toEmit = RecalculateStateLocked();
        }

        if (toEmit != null)
        {
            StateChanged?.Invoke(this, toEmit);
        }
    }

    private HqState? RecalculateStateLocked()
    {
        var now = _timeProvider();

        // 1. Expire old visual events
        _visualEvents.RemoveAll(e => (now - e.Timestamp).TotalSeconds > e.TtlSeconds);

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

        // If Demo scenario active, construct demo state
        if (!string.IsNullOrEmpty(_demoScenario))
        {
            return ApplyDemoScenarioLocked(_demoScenario, now);
        }

        // 3. Merge agents: live orca agents + active pushed agents + process agents
        var mergedAgents = new Dictionary<string, AgentSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in _orcaAgents.Values)
            mergedAgents[a.Id] = a;

        foreach (var (_, val) in _pushedAgents)
            mergedAgents[val.Agent.Id] = val.Agent;

        foreach (var a in _processAgents.Values)
        {
            if (!mergedAgents.ContainsKey(a.Id))
                mergedAgents[a.Id] = a;
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
                    // Live agent celebration expired: relax to Idle
                    finalAgents.Add(a with { State = AgentState.Idle });
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

        _currentState = newState;
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
                _visualEvents.Add(new HqEvent(seq, "CloneSpawned", $"Clone {clone.Name} spawned", now, id, 8.0));
            }
        }

        foreach (var (id, clone) in prevClones)
        {
            if (!currClones.ContainsKey(id))
            {
                var seq = Interlocked.Increment(ref _sequenceCounter);
                _visualEvents.Add(new HqEvent(seq, "CloneDespawned", $"Clone {clone.Name} despawned", now, id, 8.0));
            }
        }

        // Approval raised
        var prevAgentMap = prevAgents.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var ag in currentAgents)
        {
            if (ag.State == AgentState.NeedsApproval)
            {
                var wasNeedsApproval = prevAgentMap.TryGetValue(ag.Id, out var prev) && prev.State == AgentState.NeedsApproval;
                if (!wasNeedsApproval)
                {
                    var seq = Interlocked.Increment(ref _sequenceCounter);
                    _visualEvents.Add(new HqEvent(seq, "ApprovalRaised", $"{ag.Name} needs approval: {ag.QuestionPrompt ?? "Pending question"}", now, ag.Id, 15.0));
                }
            }
        }

        // Build pass / fail transitions
        var prevBuildMap = prevBuilds.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var b in currentBuilds)
        {
            var hadPrev = prevBuildMap.TryGetValue(b.Id, out var prevB);

            if (b.IsSuccessful && (!hadPrev || !prevB!.IsSuccessful))
            {
                var seq = Interlocked.Increment(ref _sequenceCounter);
                _visualEvents.Add(new HqEvent(seq, "BuildPassed", $"Build {b.Repo} passed", now, null, 10.0));
            }
            else if (b.IsFailing && (!hadPrev || !prevB!.IsFailing))
            {
                var seq = Interlocked.Increment(ref _sequenceCounter);
                _visualEvents.Add(new HqEvent(seq, "BuildFailed", $"Build {b.Repo} failed", now, null, 12.0));
            }
        }
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
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru (Team Leader)", AgentRole.Leader, "shikamaru", AgentState.Working, ToolName: "execute", TaskTitle: "Refactoring state engine", IsBotHome: true));
                agents.Add(new AgentSnapshot("demo-coder", "Minato (Senior Coder)", AgentRole.Coder, "minato", AgentState.Working, ToolName: "compile", TaskTitle: "Writing unit tests", IsBotHome: true));
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
                agents.Add(new AgentSnapshot("demo-leader", "Shikamaru (Team Leader)", AgentRole.Leader, "shikamaru", AgentState.Working, IsBotHome: true));
                agents.Add(new AgentSnapshot("demo-clone-1", "Shadow Clone #1", AgentRole.Clone, "clone", AgentState.Working, WorktreeId: "wt:worker-1"));
                agents.Add(new AgentSnapshot("demo-clone-2", "Shadow Clone #2", AgentRole.Clone, "clone", AgentState.Working, WorktreeId: "wt:worker-2"));
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

        _currentState = demoState;
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

        // 4. Celebrating (Done or BuildPassed)
        if (agents.Any(a => a.State == AgentState.Done) ||
            visualEvents.Any(e => string.Equals(e.Type, "BuildPassed", StringComparison.OrdinalIgnoreCase)))
        {
            return HqMood.Celebrating;
        }

        // 5. Calm (Idle / default)
        return HqMood.Calm;
    }
}
