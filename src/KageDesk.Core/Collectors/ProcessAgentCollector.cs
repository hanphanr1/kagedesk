using System.ComponentModel;
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
    private readonly object _lock = new();

    public bool Enabled { get; set; }

    public ProcessAgentCollector(
        HqStateAggregator aggregator,
        bool enabled = true,
        IProcessInspectionProvider? inspector = null,
        Func<DateTimeOffset>? timeProvider = null)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        Enabled = enabled;
        _inspector = inspector ?? Win32ProcessInspector.Instance;
        _timeProvider = timeProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<AgentSnapshot> DetectProcesses()
    {
        if (!Enabled)
            return Array.Empty<AgentSnapshot>();

        try
        {
            var processes = _inspector.InspectProcesses();
            if (processes.Count == 0)
            {
                lock (_lock) { _history.Clear(); }
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

                string exeLower = p.ExeName.ToLowerInvariant();
                string cmdLower = p.CommandLine?.ToLowerInvariant() ?? string.Empty;

                // 1. omp.exe (exclude __omp_worker_ helper daemons)
                if (exeLower is "omp.exe" or "omp")
                {
                    if (cmdLower.Contains("__omp_worker_"))
                        continue;

                    candidates.Add(new MatchedCandidate(
                        p.ProcessId,
                        p.StartTimeTicks,
                        "OMP",
                        "omp",
                        AgentRole.Coder,
                        "omp"
                    ));
                    continue;
                }

                // 2. agy.exe or antigravity.exe
                if (exeLower is "agy.exe" or "agy" or "antigravity.exe" or "antigravity")
                {
                    if (cmdLower.Contains("--type=") ||
                        cmdLower.Contains("--crashpad") ||
                        cmdLower.Contains("crashpad-handler"))
                    {
                        continue;
                    }

                    candidates.Add(new MatchedCandidate(
                        p.ProcessId,
                        p.StartTimeTicks,
                        "AGY",
                        "agy",
                        AgentRole.Leader,
                        "agy"
                    ));
                    continue;
                }

                // 3. claude.exe OR node.exe / bun.exe running claude-code
                if (exeLower is "claude.exe" or "claude")
                {
                    candidates.Add(new MatchedCandidate(
                        p.ProcessId,
                        p.StartTimeTicks,
                        "CLAUDE",
                        "claude",
                        AgentRole.Coder,
                        "claude"
                    ));
                    continue;
                }
                if (exeLower is "node.exe" or "node" or "bun.exe" or "bun")
                {
                    if (cmdLower.Contains("claude-code") ||
                        cmdLower.Contains("@anthropic-ai/claude") ||
                        cmdLower.Contains("\\claude.js") ||
                        cmdLower.Contains("/claude.js") ||
                        cmdLower.Contains("/claude") ||
                        cmdLower.Contains("\\claude"))
                    {
                        candidates.Add(new MatchedCandidate(
                            p.ProcessId,
                            p.StartTimeTicks,
                            "CLAUDE",
                            "claude",
                            AgentRole.Coder,
                            "claude"
                        ));
                        continue;
                    }
                }

                // 4. codex.exe OR node.exe / bun.exe running codex
                if (exeLower is "codex.exe" or "codex")
                {
                    candidates.Add(new MatchedCandidate(
                        p.ProcessId,
                        p.StartTimeTicks,
                        "CODEX",
                        "codex",
                        AgentRole.Coder,
                        "codex"
                    ));
                    continue;
                }
                if (exeLower is "node.exe" or "node" or "bun.exe" or "bun")
                {
                    if (cmdLower.Contains("@openai/codex") ||
                        cmdLower.Contains("\\codex") ||
                        cmdLower.Contains("/codex"))
                    {
                        candidates.Add(new MatchedCandidate(
                            p.ProcessId,
                            p.StartTimeTicks,
                            "CODEX",
                            "codex",
                            AgentRole.Coder,
                            "codex"
                        ));
                        continue;
                    }
                }

                // 5. opencode.exe OR node.exe / bun.exe running opencode
                if (exeLower is "opencode.exe" or "opencode")
                {
                    candidates.Add(new MatchedCandidate(
                        p.ProcessId,
                        p.StartTimeTicks,
                        "OPENCODE",
                        "opencode",
                        AgentRole.External,
                        "generic"
                    ));
                    continue;
                }
                if (exeLower is "node.exe" or "node" or "bun.exe" or "bun")
                {
                    if (cmdLower.Contains("opencode"))
                    {
                        candidates.Add(new MatchedCandidate(
                            p.ProcessId,
                            p.StartTimeTicks,
                            "OPENCODE",
                            "opencode",
                            AgentRole.External,
                            "generic"
                        ));
                        continue;
                    }
                }

                // 6. gemini.exe OR node.exe / bun.exe running gemini-cli
                if (exeLower is "gemini.exe" or "gemini")
                {
                    candidates.Add(new MatchedCandidate(
                        p.ProcessId,
                        p.StartTimeTicks,
                        "GEMINI",
                        "gemini",
                        AgentRole.External,
                        "agy"
                    ));
                    continue;
                }
                if (exeLower is "node.exe" or "node" or "bun.exe" or "bun")
                {
                    if (cmdLower.Contains("gemini-cli") ||
                        cmdLower.Contains("@google/gemini"))
                    {
                        candidates.Add(new MatchedCandidate(
                            p.ProcessId,
                            p.StartTimeTicks,
                            "GEMINI",
                            "gemini",
                            AgentRole.External,
                            "agy"
                        ));
                        continue;
                    }
                }
            }

            var detected = new List<AgentSnapshot>(candidates.Count);

            lock (_lock)
            {
                var liveCandidatePids = new HashSet<int>(candidates.Count);

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

                        string name = multi ? $"{cand.Provider} #{i + 1}" : cand.Provider;
                        string id = $"proc:{cand.ProviderLower}:{cand.Pid}";

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
                            LastActivityAt: stateEntry.WorkingStartedAt ?? stateEntry.FirstSeenAt,
                            UpdatedAt: now
                        ));
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
                string exeLower = item.ExeName.ToLowerInvariant();
                string cmdLower = item.CommandLine?.ToLowerInvariant() ?? string.Empty;

                // Exclude __omp_worker_* daemons and console hosts
                if (cmdLower.Contains("__omp_worker_") ||
                    exeLower is "conhost.exe" or "openconsole.exe")
                {
                    continue;
                }

                // Check priority
                // 1. git.exe, gh.exe -> "git"
                if (exeLower is "git.exe" or "git" or "gh.exe" or "gh")
                {
                    if (bestPriority > 1) { bestPriority = 1; bestTool = "git"; }
                }
                // 2. vstest*, testhost*, pytest*, vitest*, jest* -> "test"
                else if (exeLower.StartsWith("vstest", StringComparison.OrdinalIgnoreCase) ||
                         exeLower.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) ||
                         exeLower.StartsWith("pytest", StringComparison.OrdinalIgnoreCase) ||
                         exeLower.StartsWith("vitest", StringComparison.OrdinalIgnoreCase) ||
                         exeLower.StartsWith("jest", StringComparison.OrdinalIgnoreCase) ||
                         cmdLower.Contains("pytest") || cmdLower.Contains("vitest") || cmdLower.Contains("jest"))
                {
                    if (bestPriority > 2) { bestPriority = 2; bestTool = "test"; }
                }
                // 3. dotnet.exe, msbuild.exe, csc.exe, cargo.exe, rustc.exe, npm*, pnpm*, tsc*, esbuild*, vite*, gcc.exe, clang.exe -> "build"
                else if (exeLower is "dotnet.exe" or "dotnet" or "msbuild.exe" or "msbuild" or "csc.exe" or "csc" or
                         "cargo.exe" or "cargo" or "rustc.exe" or "rustc" or "gcc.exe" or "clang.exe" or
                         "tsc.exe" or "esbuild.exe" or "vite.exe" ||
                         exeLower.StartsWith("npm", StringComparison.OrdinalIgnoreCase) ||
                         exeLower.StartsWith("pnpm", StringComparison.OrdinalIgnoreCase) ||
                         cmdLower.Contains("dotnet build") || cmdLower.Contains("msbuild") || cmdLower.Contains("cargo build"))
                {
                    if (bestPriority > 3) { bestPriority = 3; bestTool = "build"; }
                }
                // 4. rg.exe, grep.exe, findstr.exe, fd.exe -> "search"
                else if (exeLower is "rg.exe" or "rg" or "grep.exe" or "grep" or "findstr.exe" or "findstr" or "fd.exe" or "fd")
                {
                    if (bestPriority > 4) { bestPriority = 4; bestTool = "search"; }
                }
                // 5. pwsh.exe, powershell.exe, cmd.exe, bash.exe, sh.exe, python.exe, node.exe -> "execute"
                else if (exeLower is "pwsh.exe" or "pwsh" or "powershell.exe" or "powershell" or "cmd.exe" or
                         "bash.exe" or "bash" or "sh.exe" or "sh" or "python.exe" or "python" or "node.exe")
                {
                    if (bestPriority > 5) { bestPriority = 5; bestTool = "execute"; }
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
                string cmd = childItem.CommandLine ?? string.Empty;
                if (cmd.Contains("__omp_worker_"))
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
        if (!Enabled) return;
        var detected = DetectProcesses();
        _aggregator.UpdateProcessAgents(detected);
    }
}
