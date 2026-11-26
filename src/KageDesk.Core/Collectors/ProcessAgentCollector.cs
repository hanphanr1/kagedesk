using System.ComponentModel;
using System.Security.Cryptography;
using KageDesk.Core.Models;
using KageDesk.Core.State;

namespace KageDesk.Core.Collectors;

public class ProcessAgentCollector
{
    private record MatchedCandidate(
        int Pid,
        long StartTimeTicks,
        string Provider,
        string ProviderLower,
        AgentRole Role,
        string SpriteKey
    );

    private class AgentPidState
    {
        public int Pid { get; set; }
        public long StartTimeTicks { get; set; }
        public AgentState State { get; set; } = AgentState.Idle;
        public string? ToolName { get; set; }
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset? WorkingStartedAt { get; set; }
        public DateTimeOffset? DoneStartedAt { get; set; }
        public long LastCpuTimeMs { get; set; }
        public bool HasValidIoSample { get; set; }
        public long LastReadBytes { get; set; }
        public long LastOtherBytes { get; set; }
        public DateTimeOffset LastSampleAt { get; set; }
        public int QuietPollCount { get; set; }
        public bool IsFirstSample { get; set; } = true;
    }

    private class ChildPidState
    {
        public long LastCpuTimeMs { get; set; }
        public bool HasValidIoSample { get; set; }
        public long LastReadBytes { get; set; }
        public long LastOtherBytes { get; set; }
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset LastSampleAt { get; set; }
        public bool IsFirstSample { get; set; } = true;
    }

    private readonly HqStateAggregator _aggregator;
    private readonly IProcessInspectionProvider _inspector;
    private readonly Func<DateTimeOffset> _timeProvider;
    private readonly AgentAppearanceRegistry _appearanceRegistry;
    private readonly Dictionary<int, AgentPidState> _history = new();
    private readonly Dictionary<(int Pid, long StartTicks), ChildPidState> _childHistory = new();
    private readonly Dictionary<(int Pid, long StartTicks), string> _opaqueTokens = new();
    private readonly HashSet<string> _usedTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private long _generation;
    private bool _enabled;

    public long Generation
    {
        get
        {
            lock (_lock)
            {
                return _generation;
            }
        }
    }

    internal int HistoryCount
    {
        get
        {
            lock (_lock)
            {
                return _history.Count;
            }
        }
    }

    internal int OpaqueTokensCount
    {
        get
        {
            lock (_lock)
            {
                return _opaqueTokens.Count;
            }
        }
    }

    public bool Enabled
    {
        get
        {
            lock (_lock)
            {
                return _enabled;
            }
        }
        set
        {
            bool shouldClearInspector = false;
            lock (_lock)
            {
                if (_enabled == value) return;
                _enabled = value;
                if (!value)
                {
                    _generation++;
                    _history.Clear();
                    _childHistory.Clear();
                    _opaqueTokens.Clear();
                    _usedTokens.Clear();
                    _appearanceRegistry.Clear();
                    shouldClearInspector = true;
                }
            }

            if (shouldClearInspector)
            {
                _inspector.ClearCache();
                _aggregator.DisableAndClearProcessAgents();
            }
        }
    }

    public ProcessAgentCollector(
        HqStateAggregator aggregator,
        bool enabled = true,
        IProcessInspectionProvider? inspector = null,
        Func<DateTimeOffset>? timeProvider = null,
        int? appearanceSeed = null)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _inspector = inspector ?? Win32ProcessInspector.Instance;
        _timeProvider = timeProvider ?? (() => DateTimeOffset.UtcNow);
        _appearanceRegistry = new AgentAppearanceRegistry(appearanceSeed);
        _enabled = enabled;
    }

    private string GetOrCreateOpaqueToken(int pid, long startTicks)
    {
        if (startTicks <= 0)
        {
            return CreateEphemeralOpaqueToken();
        }

        var key = (pid, startTicks);
        if (_opaqueTokens.TryGetValue(key, out var token))
        {
            return token;
        }

        do
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        } while (!_usedTokens.Add(token));

        _opaqueTokens[key] = token;
        return token;
    }

    private static string CreateEphemeralOpaqueToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    }

    public IReadOnlyList<AgentSnapshot> DetectProcesses()
    {
        long capturedGen;
        lock (_lock)
        {
            if (!_enabled)
                return Array.Empty<AgentSnapshot>();
            capturedGen = _generation;
        }

        try
        {
            var processes = _inspector.InspectProcesses();
            if (processes == null || processes.Count == 0)
            {
                bool clearInspector = false;
                lock (_lock)
                {
                    if (!_enabled || _generation != capturedGen)
                    {
                        clearInspector = !_enabled;
                    }
                    else
                    {
                        _history.Clear();
                        _childHistory.Clear();
                        _opaqueTokens.Clear();
                        _usedTokens.Clear();
                        _appearanceRegistry.Clear();
                    }
                }

                if (clearInspector)
                {
                    _inspector.ClearCache();
                }

                return Array.Empty<AgentSnapshot>();
            }

            var now = _timeProvider();

            // Build verified parent-child maps (reject PID-reuse edges where parent is younger than child or timestamps are unverified)
            var childrenMap = new Dictionary<int, List<int>>(processes.Count);
            var procByPid = new Dictionary<int, ProcessItem>(processes.Count);

            foreach (var p in processes)
            {
                procByPid[p.ProcessId] = p;
            }

            foreach (var p in processes)
            {
                if (p.ParentProcessId <= 0 || p.ParentProcessId == p.ProcessId)
                {
                    continue;
                }

                if (procByPid.TryGetValue(p.ParentProcessId, out var parentProc) &&
                    Win32ProcessInspector.IsValidParentChildEdge(parentProc, p))
                {
                    if (!childrenMap.TryGetValue(p.ParentProcessId, out var childList))
                    {
                        childList = new List<int>();
                        childrenMap[p.ParentProcessId] = childList;
                    }
                    childList.Add(p.ProcessId);
                }
            }

            // Identify all Orca.exe processes and their verified descendants
            var orcaPids = new HashSet<int>();
            foreach (var p in processes)
            {
                if (Win32ProcessInspector.IsOrcaExecutable(p.ExeName))
                {
                    orcaPids.Add(p.ProcessId);
                }
            }

            var orcaAndDescendants = new HashSet<int>(orcaPids);
            var queue = new Queue<int>(orcaPids);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                if (childrenMap.TryGetValue(cur, out var cList))
                {
                    foreach (var c in cList)
                    {
                        if (orcaAndDescendants.Add(c))
                        {
                            queue.Enqueue(c);
                        }
                    }
                }
            }

            // Identify standalone terminal agent candidates
            var candidates = new List<MatchedCandidate>();

            foreach (var p in processes)
            {
                if (orcaAndDescendants.Contains(p.ProcessId))
                {
                    continue; // Exclude Orca and any of its child processes
                }

                var cl = p.Classification ?? Win32ProcessInspector.ClassifyProcess(p.ExeName, p.CommandLine);
                if (cl.IsInternalDaemon || cl.IsElectronHelper || cl.Provider == null)
                {
                    continue;
                }

                (AgentRole role, string spriteKey) = cl.Provider switch
                {
                    "OMP" => (AgentRole.Coder, "omp"),
                    "AGY" => (AgentRole.Leader, "agy"),
                    "CLAUDE" => (AgentRole.Coder, "claude"),
                    "CODEX" => (AgentRole.Coder, "codex"),
                    "OPENCODE" => (AgentRole.External, "generic"),
                    "GEMINI" => (AgentRole.External, "agy"),
                    _ => (AgentRole.Coder, "generic")
                };

                candidates.Add(new MatchedCandidate(
                    p.ProcessId,
                    p.StartTimeTicks,
                    cl.Provider,
                    cl.Provider.ToLowerInvariant(),
                    role,
                    spriteKey
                ));
            }

            var detected = new List<AgentSnapshot>(candidates.Count);
            bool clearInspectorOnDisabled = false;
            bool generationMismatch = false;

            lock (_lock)
            {
                if (!_enabled || _generation != capturedGen)
                {
                    generationMismatch = true;
                    clearInspectorOnDisabled = !_enabled;
                }
                else
                {
                    var liveCandidatePids = new HashSet<int>(candidates.Count);
                    var liveKeys = new HashSet<(int Pid, long StartTicks)>(candidates.Count);
                    var liveChildKeys = new HashSet<(int Pid, long StartTicks)>();
                    var liveAgentIds = new HashSet<string>(candidates.Count, StringComparer.OrdinalIgnoreCase);
                    var candidateTokens = new Dictionary<MatchedCandidate, string>(candidates.Count);

                    // Pre-compute live agent IDs so appearance registry only evicts truly departed sessions
                    foreach (var cand in candidates)
                    {
                        string tok = GetOrCreateOpaqueToken(cand.Pid, cand.StartTimeTicks);
                        candidateTokens[cand] = tok;
                        liveAgentIds.Add($"proc:{cand.ProviderLower}:{tok}");
                    }

                    // Group by Provider to number multiple instances cleanly (OMP, OMP 2, Claude, Claude 2)
                    var byProvider = candidates.GroupBy(c => c.Provider, StringComparer.OrdinalIgnoreCase);

                    foreach (var group in byProvider)
                    {
                        // Sort stably by start time, then PID
                        var sorted = group.OrderBy(c => c.StartTimeTicks).ThenBy(c => c.Pid).ToList();

                        for (int i = 0; i < sorted.Count; i++)
                        {
                            var cand = sorted[i];
                            bool hasVerifiedStartTime = cand.StartTimeTicks > 0;
                            if (hasVerifiedStartTime)
                            {
                                liveCandidatePids.Add(cand.Pid);
                                liveKeys.Add((cand.Pid, cand.StartTimeTicks));
                            }

                            string name = AgentAppearanceRegistry.FormatIndexedProviderName(cand.Provider, i, sorted.Count);
                            string opaqueToken = candidateTokens[cand];
                            string id = $"proc:{cand.ProviderLower}:{opaqueToken}";

                            AgentPidState stateEntry;
                            if (hasVerifiedStartTime)
                            {
                                if (!_history.TryGetValue(cand.Pid, out stateEntry!) || stateEntry.StartTimeTicks != cand.StartTimeTicks)
                                {
                                    stateEntry = new AgentPidState
                                    {
                                        Pid = cand.Pid,
                                        StartTimeTicks = cand.StartTimeTicks,
                                        FirstSeenAt = now,
                                        LastSampleAt = now,
                                        IsFirstSample = true
                                    };
                                    _history[cand.Pid] = stateEntry;
                                }
                            }
                            else
                            {
                                // Unverified StartTimeTicks == 0: never reuse prior history keyed on 0
                                stateEntry = new AgentPidState
                                {
                                    Pid = cand.Pid,
                                    StartTimeTicks = 0,
                                    FirstSeenAt = now,
                                    LastSampleAt = now,
                                    IsFirstSample = true
                                };
                            }

                            bool wasAgentFirstSample = stateEntry.IsFirstSample;

                            // 1. Detect Active Tool Child (ignores console hosts, internal/MCP/LSP daemons as tools themselves, while traversing descendants)
                            string? detectedTool = hasVerifiedStartTime
                                ? DetectActiveToolChild(
                                    cand.Pid,
                                    wasAgentFirstSample,
                                    now,
                                    childrenMap,
                                    procByPid,
                                    liveChildKeys)
                                : null;

                            // 2. Read agent's own CPU and IO counters (exclude background __omp_worker_* daemon CPU)
                            procByPid.TryGetValue(cand.Pid, out var agentProc);
                            long totalCpuMs = agentProc?.TotalCpuTimeMs ?? 0;
                            bool hasIoCounters = agentProc?.HasIoCounters ?? false;
                            long readBytes = agentProc?.ReadTransferBytes ?? 0;
                            long otherBytes = agentProc?.OtherTransferBytes ?? 0;

                            double elapsedWallMs = (now - stateEntry.LastSampleAt).TotalMilliseconds;
                            long cpuDeltaMs = Math.Max(0, totalCpuMs - stateEntry.LastCpuTimeMs);
                            double cpuRatio = elapsedWallMs > 0 ? (double)cpuDeltaMs / elapsedWallMs : 0;

                            bool prevHadValidIo = stateEntry.HasValidIoSample;
                            bool hasValidIoDelta = !stateEntry.IsFirstSample && prevHadValidIo && hasIoCounters;
                            long readBytesDelta = hasValidIoDelta ? Math.Max(0, readBytes - stateEntry.LastReadBytes) : 0;
                            long otherBytesDelta = hasValidIoDelta ? Math.Max(0, otherBytes - stateEntry.LastOtherBytes) : 0;
                            double elapsedSec = elapsedWallMs / 1000.0;
                            double otherBytesPerSec = elapsedSec > 0 ? otherBytesDelta / elapsedSec : 0;

                            if (stateEntry.IsFirstSample)
                            {
                                stateEntry.IsFirstSample = false;
                                stateEntry.LastCpuTimeMs = totalCpuMs;
                                if (hasIoCounters)
                                {
                                    stateEntry.HasValidIoSample = true;
                                    stateEntry.LastReadBytes = readBytes;
                                    stateEntry.LastOtherBytes = otherBytes;
                                }
                                else
                                {
                                    stateEntry.HasValidIoSample = false;
                                }
                                stateEntry.LastSampleAt = now;

                                if (detectedTool != null)
                                {
                                    stateEntry.State = AgentState.Working;
                                    stateEntry.ToolName = detectedTool;
                                    stateEntry.WorkingStartedAt = now;
                                }
                                else
                                {
                                    stateEntry.State = AgentState.Idle;
                                    stateEntry.ToolName = null;
                                    stateEntry.WorkingStartedAt = null;
                                }
                            }
                            else
                            {
                                stateEntry.LastCpuTimeMs = totalCpuMs;
                                if (hasIoCounters)
                                {
                                    stateEntry.HasValidIoSample = true;
                                    stateEntry.LastReadBytes = readBytes;
                                    stateEntry.LastOtherBytes = otherBytes;
                                }
                                else
                                {
                                    // Never store 0 as an I/O baseline when GetProcessIoCounters fails
                                    stateEntry.HasValidIoSample = false;
                                }
                                stateEntry.LastSampleAt = now;

                                bool isCpuActive;
                                if (hasIoCounters)
                                {
                                    if (hasValidIoDelta)
                                    {
                                        // Streaming sessions transferring kilobytes of I/O even at low CPU (~8-10ms)
                                        // classify as Working, while tiny idle TUI redraw/cursor blink jitter stays Idle.
                                        bool hasMeaningfulIo = readBytesDelta >= 256 || otherBytesPerSec >= 512.0;
                                        bool hasStreamingIo = readBytesDelta >= 1024 ||
                                            (otherBytesDelta >= 1024 && (elapsedSec <= 0 || otherBytesPerSec >= 512.0));
                                        isCpuActive = hasStreamingIo
                                                   || (cpuDeltaMs >= 45.0 && cpuRatio >= 0.02 && (hasMeaningfulIo || cpuRatio >= 0.35));
                                    }
                                    else
                                    {
                                        // Rebased sample after failed I/O counters: never compare lifetime counters against 0
                                        isCpuActive = cpuDeltaMs >= 45.0 && cpuRatio >= 0.35;
                                    }
                                }
                                else
                                {
                                    // Reject small TUI blink/timer CPU ripples (< 45ms or < 2.0% core)
                                    isCpuActive = cpuDeltaMs >= 45.0 && cpuRatio >= 0.02;
                                }

                                bool isActive = detectedTool != null || isCpuActive;

                                if (isActive)
                                {
                                    stateEntry.QuietPollCount = 0;
                                    if (stateEntry.State != AgentState.Working)
                                    {
                                        stateEntry.WorkingStartedAt = now;
                                    }
                                    stateEntry.State = AgentState.Working;
                                    stateEntry.ToolName = detectedTool ?? "thinking";
                                    stateEntry.DoneStartedAt = null;
                                }
                                else
                                {
                                    stateEntry.QuietPollCount++;

                                    if (stateEntry.State == AgentState.Working && stateEntry.QuietPollCount == 1)
                                    {
                                        // 1-poll hysteresis bridges short gaps between bursts
                                        stateEntry.ToolName = "thinking";
                                    }
                                    else if (stateEntry.QuietPollCount >= 2)
                                    {
                                        bool wasWorkingLongEnough = stateEntry.WorkingStartedAt.HasValue &&
                                            (now - stateEntry.WorkingStartedAt.Value).TotalSeconds >= 2.0;

                                        if (wasWorkingLongEnough && stateEntry.State == AgentState.Working)
                                        {
                                            // Transition cleanly to brief Done celebration, clearing work timer
                                            stateEntry.State = AgentState.Done;
                                            stateEntry.DoneStartedAt = now;
                                            stateEntry.WorkingStartedAt = null;
                                            stateEntry.ToolName = null;
                                        }
                                        else if (stateEntry.State == AgentState.Done)
                                        {
                                            if (stateEntry.DoneStartedAt.HasValue &&
                                                (now - stateEntry.DoneStartedAt.Value).TotalSeconds < 5.0)
                                            {
                                                stateEntry.State = AgentState.Done;
                                            }
                                            else
                                            {
                                                stateEntry.State = AgentState.Idle;
                                                stateEntry.DoneStartedAt = null;
                                                stateEntry.WorkingStartedAt = null;
                                                stateEntry.ToolName = null;
                                            }
                                        }
                                        else
                                        {
                                            stateEntry.State = AgentState.Idle;
                                            stateEntry.DoneStartedAt = null;
                                            stateEntry.WorkingStartedAt = null;
                                            stateEntry.ToolName = null;
                                        }
                                    }
                                }
                            }

                            var appearance = _appearanceRegistry.GetOrAssignForNonOrca(id, liveAgentIds);

                            detected.Add(new AgentSnapshot(
                                Id: id,
                                Name: name,
                                Role: cand.Role,
                                SpriteKey: appearance.SpriteKey,
                                State: stateEntry.State,
                                ToolName: stateEntry.ToolName,
                                Source: "process",
                                WorkStartedAt: stateEntry.State == AgentState.Working ? stateEntry.WorkingStartedAt : null,
                                ProcessId: hasVerifiedStartTime ? cand.Pid : null,
                                ProcessStartTicks: hasVerifiedStartTime ? cand.StartTimeTicks : null,
                                LastActivityAt: stateEntry.WorkingStartedAt ?? stateEntry.FirstSeenAt,
                                UpdatedAt: now,
                                DeskRig: appearance.DeskRig,
                                BehaviorVariant: appearance.BehaviorVariant
                            ));
                        }
                    }

                    // Evict stale opaque tokens
                    var staleTokenKeys = _opaqueTokens.Keys.Where(k => !liveKeys.Contains(k)).ToList();
                    foreach (var k in staleTokenKeys)
                    {
                        if (_opaqueTokens.Remove(k, out var token))
                        {
                            _usedTokens.Remove(token);
                        }
                    }

                    // Evict exited PIDs cleanly
                    if (_history.Count > liveCandidatePids.Count)
                    {
                        var stale = _history.Keys.Where(k => !liveCandidatePids.Contains(k)).ToList();
                        foreach (var s in stale)
                        {
                            _history.Remove(s);
                        }
                    }

                    // Evict exited child states cleanly
                    if (_childHistory.Count > liveChildKeys.Count)
                    {
                        var staleChildren = _childHistory.Keys.Where(k => !liveChildKeys.Contains(k)).ToList();
                        foreach (var sc in staleChildren)
                        {
                            _childHistory.Remove(sc);
                        }
                    }
                }
            }

            if (clearInspectorOnDisabled)
            {
                _inspector.ClearCache();
            }

            if (generationMismatch)
            {
                return Array.Empty<AgentSnapshot>();
            }

            return detected;
        }
        catch (Exception)
        {
            return Array.Empty<AgentSnapshot>();
        }
    }

    private string? DetectActiveToolChild(
        int agentPid,
        bool agentIsFirstSample,
        DateTimeOffset now,
        Dictionary<int, List<int>> childrenMap,
        Dictionary<int, ProcessItem> procByPid,
        HashSet<(int Pid, long StartTicks)> liveChildKeys)
    {
        // Breadth-first walk up to depth 4 (allows traversing through __omp_worker_* / console hosts to spawned tools)
        var queue = new Queue<(int Pid, int Depth)>();
        if (childrenMap.TryGetValue(agentPid, out var directChildren))
        {
            foreach (var c in directChildren)
            {
                queue.Enqueue((c, 1));
            }
        }

        string? bestTool = null;
        int bestPriority = int.MaxValue;

        var visited = new HashSet<int> { agentPid };

        while (queue.Count > 0)
        {
            var (cPid, depth) = queue.Dequeue();
            if (!visited.Add(cPid)) continue;

            if (procByPid.TryGetValue(cPid, out var item))
            {
                var cl = item.Classification ?? Win32ProcessInspector.ClassifyProcess(item.ExeName, item.CommandLine);

                // Exclude __omp_worker_* daemons and console hosts from marking the agent working themselves,
                // while still traversing their descendants to detect real foreground tools spawned underneath.
                bool isExcludedWrapper = cl.IsInternalDaemon ||
                    item.ExeName.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) ||
                    item.ExeName.Equals("openconsole.exe", StringComparison.OrdinalIgnoreCase);

                if (!isExcludedWrapper && cl.RoleHint != null)
                {
                    bool hasVerifiedChildStart = item.StartTimeTicks > 0;
                    ChildPidState childState;
                    if (hasVerifiedChildStart)
                    {
                        var childKey = (item.ProcessId, item.StartTimeTicks);
                        liveChildKeys.Add(childKey);

                        if (!_childHistory.TryGetValue(childKey, out childState!))
                        {
                            childState = new ChildPidState
                            {
                                LastCpuTimeMs = item.TotalCpuTimeMs,
                                HasValidIoSample = item.HasIoCounters,
                                LastReadBytes = item.HasIoCounters ? item.ReadTransferBytes : 0,
                                LastOtherBytes = item.HasIoCounters ? item.OtherTransferBytes : 0,
                                FirstSeenAt = now,
                                LastSampleAt = now,
                                IsFirstSample = true
                            };
                            _childHistory[childKey] = childState;
                        }
                    }
                    else
                    {
                        childState = new ChildPidState
                        {
                            LastCpuTimeMs = item.TotalCpuTimeMs,
                            HasValidIoSample = item.HasIoCounters,
                            LastReadBytes = item.HasIoCounters ? item.ReadTransferBytes : 0,
                            LastOtherBytes = item.HasIoCounters ? item.OtherTransferBytes : 0,
                            FirstSeenAt = now,
                            LastSampleAt = now,
                            IsFirstSample = true
                        };
                    }

                    bool isChildFirstSample = childState.IsFirstSample;
                    double childElapsedMs = (now - childState.LastSampleAt).TotalMilliseconds;
                    long childCpuDeltaMs = Math.Max(0, item.TotalCpuTimeMs - childState.LastCpuTimeMs);
                    double childCpuRatio = childElapsedMs > 0 ? (double)childCpuDeltaMs / childElapsedMs : 0;

                    bool childHasIo = item.HasIoCounters;
                    bool childHasValidIoDelta = !isChildFirstSample && childState.HasValidIoSample && childHasIo;
                    long childReadDelta = childHasValidIoDelta ? Math.Max(0, item.ReadTransferBytes - childState.LastReadBytes) : 0;
                    long childOtherDelta = childHasValidIoDelta ? Math.Max(0, item.OtherTransferBytes - childState.LastOtherBytes) : 0;
                    double childElapsedSec = childElapsedMs / 1000.0;
                    double childOtherPerSec = childElapsedSec > 0 ? childOtherDelta / childElapsedSec : 0;
                    bool childHasStreamingIo = childHasValidIoDelta &&
                        (childReadDelta >= 1024 || (childOtherDelta >= 1024 && (childElapsedSec <= 0 || childOtherPerSec >= 512.0)));

                    childState.IsFirstSample = false;
                    childState.LastCpuTimeMs = item.TotalCpuTimeMs;
                    if (childHasIo)
                    {
                        childState.HasValidIoSample = true;
                        childState.LastReadBytes = item.ReadTransferBytes;
                        childState.LastOtherBytes = item.OtherTransferBytes;
                    }
                    else
                    {
                        childState.HasValidIoSample = false;
                    }
                    childState.LastSampleAt = now;

                    bool isStrongTransientTool = cl.RoleHint is "git" or "test" or "build" or "search";
                    bool isChildActive;

                    if (cl.IsPersistentDaemon)
                    {
                        // Long-lived MCP/LSP servers only count if actively burning substantial CPU or streaming I/O
                        isChildActive = !isChildFirstSample &&
                            ((childCpuDeltaMs >= 30 && childCpuRatio >= 0.025) || childHasStreamingIo);
                    }
                    else if (isStrongTransientTool)
                    {
                        // Transient dev tools (git, dotnet, rg, pytest, vitest): active when freshly spawned, burning CPU, or streaming I/O
                        isChildActive = isChildFirstSample
                            || (now - childState.FirstSeenAt).TotalSeconds < 8.0
                            || childCpuDeltaMs >= 15
                            || childCpuRatio >= 0.01
                            || childHasStreamingIo;
                    }
                    else
                    {
                        // Generic shell/runtime child (node, pwsh, cmd, bash, python):
                        // Do NOT mark idle parent Working for pre-existing idle shells/MCPs;
                        // active only if newly spawned after agent tracking began OR actively consuming CPU/I/O.
                        isChildActive = isChildFirstSample
                            ? !agentIsFirstSample
                            : ((childCpuDeltaMs >= 20 && childCpuRatio >= 0.015) || childHasStreamingIo);
                    }

                    if (isChildActive)
                    {
                        int priority = cl.RoleHint switch
                        {
                            "git" => 1,
                            "test" => 2,
                            "build" => 3,
                            "search" => 4,
                            "execute" => 5,
                            _ => int.MaxValue
                        };
                        if (priority < bestPriority)
                        {
                            bestPriority = priority;
                            bestTool = cl.RoleHint;
                        }
                    }
                }
            }

            if (depth < 4 && childrenMap.TryGetValue(cPid, out var deeper))
            {
                foreach (var d in deeper)
                {
                    queue.Enqueue((d, depth + 1));
                }
            }
        }

        return bestTool;
    }

    internal Action? BeforePublishAction { get; set; }

    public void CollectOnce()
    {
        long capturedGen;
        lock (_lock)
        {
            if (!_enabled) return;
            capturedGen = _generation;
        }

        long gen = _aggregator.GetProcessGeneration();
        var detected = DetectProcesses();

        lock (_lock)
        {
            if (!_enabled || _generation != capturedGen) return;
        }

        BeforePublishAction?.Invoke();

        _aggregator.UpdateProcessAgents(detected, gen);
    }
}
