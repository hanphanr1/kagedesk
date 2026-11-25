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
        public DateTimeOffset LastSampleAt { get; set; }
        public int QuietPollCount { get; set; }
        public bool IsFirstSample { get; set; } = true;
    }

    private readonly HqStateAggregator _aggregator;
    private readonly IProcessInspectionProvider _inspector;
    private readonly Func<DateTimeOffset> _timeProvider;
    private readonly Dictionary<int, AgentPidState> _history = new();
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
                    _opaqueTokens.Clear();
                    _usedTokens.Clear();
                    shouldClearInspector = true;
                }
            }

            if (shouldClearInspector)
            {
                _inspector.ClearCache();
            }
        }
    }

    public ProcessAgentCollector(
        HqStateAggregator aggregator,
        bool enabled = true,
        IProcessInspectionProvider? inspector = null,
        Func<DateTimeOffset>? timeProvider = null)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _inspector = inspector ?? Win32ProcessInspector.Instance;
        _timeProvider = timeProvider ?? (() => DateTimeOffset.UtcNow);
        _enabled = enabled;
    }

    private string GetOrCreateOpaqueToken(int pid, long startTicks)
    {
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
                        _opaqueTokens.Clear();
                        _usedTokens.Clear();
                    }
                }

                if (clearInspector)
                {
                    _inspector.ClearCache();
                }

                return Array.Empty<AgentSnapshot>();
            }

            var now = _timeProvider();

            // Build parent and child maps
            var parentMap = new Dictionary<int, int>(processes.Count);
            var childrenMap = new Dictionary<int, List<int>>(processes.Count);
            var procByPid = new Dictionary<int, ProcessItem>(processes.Count);

            foreach (var p in processes)
            {
                procByPid[p.ProcessId] = p;
                parentMap[p.ProcessId] = p.ParentProcessId;

                if (!childrenMap.TryGetValue(p.ParentProcessId, out var childList))
                {
                    childList = new List<int>();
                    childrenMap[p.ParentProcessId] = childList;
                }
                childList.Add(p.ProcessId);
            }

            // Identify all Orca.exe processes and their descendants
            var orcaPids = new HashSet<int>();
            foreach (var p in processes)
            {
                if (string.Equals(p.ExeName, "Orca.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.ExeName, "orca", StringComparison.OrdinalIgnoreCase))
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

                // Group by Provider to number multiple instances stably
                var byProvider = candidates.GroupBy(c => c.Provider, StringComparer.OrdinalIgnoreCase);

                foreach (var group in byProvider)
                {
                    // Sort stably by start time, then PID
                    var sorted = group.OrderBy(c => c.StartTimeTicks).ThenBy(c => c.Pid).ToList();
                    bool multi = sorted.Count > 1;

                    for (int i = 0; i < sorted.Count; i++)
                    {
                        var cand = sorted[i];
                        liveCandidatePids.Add(cand.Pid);
                        liveKeys.Add((cand.Pid, cand.StartTimeTicks));

                        string name = multi ? $"{cand.Provider} #{i + 1}" : cand.Provider;
                        string opaqueToken = GetOrCreateOpaqueToken(cand.Pid, cand.StartTimeTicks);
                        string id = $"proc:{cand.ProviderLower}:{opaqueToken}";

                        if (!_history.TryGetValue(cand.Pid, out var stateEntry) || stateEntry.StartTimeTicks != cand.StartTimeTicks)
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

                        // 1. Detect Active Tool Child
                        string? detectedTool = DetectActiveToolChild(cand.Pid, childrenMap, procByPid);

                        // 2. Compute Aggregated CPU Time (agent + __omp_worker_* children)
                        long totalCpuMs = ComputeAggregatedCpu(cand.Pid, childrenMap, procByPid);

                        double elapsedWallMs = (now - stateEntry.LastSampleAt).TotalMilliseconds;
                        long cpuDeltaMs = totalCpuMs - stateEntry.LastCpuTimeMs;
                        if (cpuDeltaMs < 0) cpuDeltaMs = 0;
                        double cpuRatio = elapsedWallMs > 0 ? (double)cpuDeltaMs / elapsedWallMs : 0;

                        if (stateEntry.IsFirstSample)
                        {
                            stateEntry.IsFirstSample = false;
                            stateEntry.LastCpuTimeMs = totalCpuMs;
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
                            }
                        }
                        else
                        {
                            stateEntry.LastCpuTimeMs = totalCpuMs;
                            stateEntry.LastSampleAt = now;

                            bool isActive = detectedTool != null || cpuDeltaMs >= 18.0 || cpuRatio >= 0.008;

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
                                // Quiet poll (cpuDeltaMs < 8 ms and no active tool child)
                                stateEntry.QuietPollCount++;

                                if (stateEntry.State == AgentState.Working && stateEntry.QuietPollCount == 1)
                                {
                                    // 1-poll hysteresis holds Working
                                    stateEntry.ToolName = "thinking";
                                }
                                else if (stateEntry.QuietPollCount >= 2)
                                {
                                    bool wasWorkingLongEnough = stateEntry.WorkingStartedAt.HasValue &&
                                        (now - stateEntry.WorkingStartedAt.Value).TotalSeconds >= 2.0;

                                    if (wasWorkingLongEnough && stateEntry.State == AgentState.Working)
                                    {
                                        // Transition to Done!
                                        stateEntry.State = AgentState.Done;
                                        stateEntry.DoneStartedAt = now;
                                        stateEntry.WorkingStartedAt = null;
                                        stateEntry.ToolName = null;
                                    }
                                    else if (stateEntry.State == AgentState.Done)
                                    {
                                        if (stateEntry.DoneStartedAt.HasValue &&
                                            (now - stateEntry.DoneStartedAt.Value).TotalSeconds < 6.0)
                                        {
                                            stateEntry.State = AgentState.Done;
                                        }
                                        else
                                        {
                                            stateEntry.State = AgentState.Idle;
                                            stateEntry.WorkingStartedAt = null;
                                            stateEntry.ToolName = null;
                                        }
                                    }
                                    else
                                    {
                                        stateEntry.State = AgentState.Idle;
                                        stateEntry.WorkingStartedAt = null;
                                        stateEntry.ToolName = null;
                                    }
                                }
                            }
                        }

                        detected.Add(new AgentSnapshot(
                            Id: id,
                            Name: name,
                            Role: cand.Role,
                            SpriteKey: cand.SpriteKey,
                            State: stateEntry.State,
                            ToolName: stateEntry.ToolName,
                            Source: "process",
                            ProcessId: cand.Pid,
                            ProcessStartTicks: cand.StartTimeTicks,
                            LastActivityAt: stateEntry.WorkingStartedAt ?? stateEntry.FirstSeenAt,
                            UpdatedAt: now
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

    private static string? DetectActiveToolChild(
        int agentPid,
        Dictionary<int, List<int>> childrenMap,
        Dictionary<int, ProcessItem> procByPid)
    {
        // Breadth-first walk up to depth 3
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

                // Exclude __omp_worker_* daemons and console hosts
                if (cl.IsInternalDaemon ||
                    item.ExeName.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) ||
                    item.ExeName.Equals("openconsole.exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (cl.RoleHint != null)
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

            if (depth < 3 && childrenMap.TryGetValue(cPid, out var deeper))
            {
                foreach (var d in deeper)
                {
                    queue.Enqueue((d, depth + 1));
                }
            }
        }

        return bestTool;
    }

    private static long ComputeAggregatedCpu(
        int agentPid,
        Dictionary<int, List<int>> childrenMap,
        Dictionary<int, ProcessItem> procByPid)
    {
        long sum = 0;
        if (procByPid.TryGetValue(agentPid, out var agentProc))
        {
            sum += agentProc.TotalCpuTimeMs;
        }

        // Sum direct/indirect __omp_worker_* helper children
        var queue = new Queue<int>();
        if (childrenMap.TryGetValue(agentPid, out var directChildren))
        {
            foreach (var c in directChildren)
            {
                queue.Enqueue(c);
            }
        }

        var visited = new HashSet<int> { agentPid };
        while (queue.Count > 0)
        {
            int cPid = queue.Dequeue();
            if (!visited.Add(cPid)) continue;

            if (procByPid.TryGetValue(cPid, out var childItem))
            {
                var cl = childItem.Classification ?? Win32ProcessInspector.ClassifyProcess(childItem.ExeName, childItem.CommandLine);
                if (cl.IsInternalDaemon)
                {
                    sum += childItem.TotalCpuTimeMs;
                }
            }

            if (childrenMap.TryGetValue(cPid, out var deeper))
            {
                foreach (var d in deeper)
                {
                    queue.Enqueue(d);
                }
            }
        }

        return sum;
    }

    public void CollectOnce()
    {
        long capturedGen;
        lock (_lock)
        {
            if (!_enabled) return;
            capturedGen = _generation;
        }

        var detected = DetectProcesses();

        lock (_lock)
        {
            if (!_enabled || _generation != capturedGen) return;
        }

        _aggregator.UpdateProcessAgents(detected);
    }
}
