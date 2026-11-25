using KageDesk.Core.Collectors;
using KageDesk.Core.Models;
using KageDesk.Core.State;
using Xunit;

namespace KageDesk.Core.Tests;

public class ProcessAgentCollectorTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ProcessAgentCollectorTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DetectProcesses_LiveSystem_FindsRunningAgentsWithoutCrashing()
    {
        var aggregator = new HqStateAggregator();
        var collector = new ProcessAgentCollector(aggregator, enabled: true, inspector: Win32ProcessInspector.Instance);
        var detected = collector.DetectProcesses();
        Assert.NotNull(detected);
        _output.WriteLine($"[LIVE DETECTED PROCESS AGENTS: {detected.Count}]");
        foreach (var ag in detected)
        {
            _output.WriteLine($"  - Id: {ag.Id}, Name: {ag.Name}, Source: {ag.Source}, State: {ag.State}, PID: {ag.ProcessId}");
        }
    }

    [Fact]
    public async Task DetectOrca_LiveSystem_FindsOrcaAgentsWithoutCrashing()
    {
        var aggregator = new HqStateAggregator();
        var collector = new OrcaCliCollector(aggregator);
        await collector.CollectOnceAsync();
        var state = aggregator.CurrentState;
        _output.WriteLine($"[LIVE DETECTED ORCA AGENTS: {state.Agents.Count}]");
        foreach (var ag in state.Agents)
        {
            _output.WriteLine($"  - Id: {ag.Id}, Name: {ag.Name}, Source: {ag.Source}, State: {ag.State}, Role: {ag.Role}");
        }
    }

    private class MockProcessInspector : IProcessInspectionProvider
    {
        public List<ProcessItem> Processes { get; set; } = new();

        public IReadOnlyList<ProcessItem> InspectProcesses() => Processes;
    }

    [Fact]
    public void DetectProcesses_OmpWithWorkerDaemons_AggregatesAndExcludesDaemons()
    {
        var mock = new MockProcessInspector
        {
            Processes = new List<ProcessItem>
            {
                new(100, 10, "omp.exe", "omp.exe", 1000, 20),
                new(101, 100, "omp.exe", "omp.exe --__omp_worker_daemon", 1100, 50),
                new(102, 100, "omp.exe", "omp.exe --__omp_worker_mnemopi_embed", 1200, 30)
            }
        };

        var aggregator = new HqStateAggregator();
        var collector = new ProcessAgentCollector(aggregator, enabled: true, inspector: mock);

        var detected = collector.DetectProcesses();

        var single = Assert.Single(detected);
        Assert.Matches(@"^proc:omp:[0-9a-f]{8}$", single.Id);
        Assert.NotEqual("proc:omp:100", single.Id);
        Assert.Equal(17, single.Id.Length);
        Assert.Equal("OMP", single.Name);
        Assert.Equal(AgentRole.Coder, single.Role);
        Assert.Contains(single.SpriteKey, AgentAppearanceRegistry.RandomizableSprites);
        Assert.NotEqual(DeskRigKind.Default, single.DeskRig);
        Assert.Equal(100, single.ProcessId);

        // Stable identity and character assignment across polls
        var secondPoll = collector.DetectProcesses();
        Assert.Equal(single.Id, secondPoll[0].Id);
        Assert.Equal(single.SpriteKey, secondPoll[0].SpriteKey);
        Assert.Equal(single.DeskRig, secondPoll[0].DeskRig);
        Assert.Equal(single.BehaviorVariant, secondPoll[0].BehaviorVariant);
    }

    [Fact]
    public void DetectProcesses_MultipleInstances_AreStablyNumbered()
    {
        var mock = new MockProcessInspector
        {
            Processes = new List<ProcessItem>
            {
                new(200, 10, "omp.exe", "omp.exe", 2000, 20),
                new(100, 10, "omp.exe", "omp.exe", 1000, 20)
            }
        };

        var aggregator = new HqStateAggregator();
        var collector = new ProcessAgentCollector(aggregator, enabled: true, inspector: mock);

        var detected = collector.DetectProcesses();

        Assert.Equal(2, detected.Count);
        Assert.Equal("OMP", detected[0].Name);
        Assert.Equal(100, detected[0].ProcessId);
        Assert.Equal("OMP 2", detected[1].Name);
        Assert.Equal(200, detected[1].ProcessId);
        Assert.NotEqual(detected[0].SpriteKey, detected[1].SpriteKey);
        Assert.DoesNotContain("clone", detected[0].Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clone", detected[1].Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DetectProcesses_OrcaDescendants_AreExcluded()
    {
        var mock = new MockProcessInspector
        {
            Processes = new List<ProcessItem>
            {
                new(50, 1, "Orca.exe", "Orca.exe", 100, 100),
                new(51, 50, "cmd.exe", "cmd.exe", 200, 10),
                new(52, 51, "agy.exe", "agy.exe acp", 300, 50),
                new(80, 1, "WindowsTerminal.exe", "wt.exe", 150, 100),
                new(81, 80, "agy.exe", "agy.exe", 250, 50)
            }
        };

        var aggregator = new HqStateAggregator();
        var collector = new ProcessAgentCollector(aggregator, enabled: true, inspector: mock);

        var detected = collector.DetectProcesses();

        var agent = Assert.Single(detected);
        Assert.Matches(@"^proc:agy:[0-9a-f]{8}$", agent.Id);
        Assert.NotEqual("proc:agy:81", agent.Id);
        Assert.Equal(17, agent.Id.Length);
        Assert.Equal("AGY", agent.Name);
        Assert.Contains(agent.SpriteKey, AgentAppearanceRegistry.RandomizableSprites);

        // Stable identity across polls
        var secondPoll = collector.DetectProcesses();
        Assert.Equal(agent.Id, secondPoll[0].Id);
        Assert.Equal(agent.SpriteKey, secondPoll[0].SpriteKey);
    }

    [Fact]
    public void DetectProcesses_ClaudeCodexOpencodeGemini_MatchingAndElectronExclusion()
    {
        var mock = new MockProcessInspector
        {
            Processes = new List<ProcessItem>
            {
                new(201, 1, "node.exe", "node.exe C:\\bin\\node_modules\\@anthropic-ai\\claude-code\\cli.js", 1000, 10),
                new(202, 1, "node.exe", "node.exe C:\\bin\\node_modules\\@openai\\codex\\bin.js", 1000, 10),
                new(203, 1, "opencode.exe", "opencode.exe", 1000, 10),
                new(204, 1, "node.exe", "node.exe C:\\bin\\node_modules\\gemini-cli\\index.js", 1000, 10),
                // Electron renderer / crashpad should be excluded
                new(205, 1, "agy.exe", "agy.exe --type=renderer --field-trial-handle=123", 1000, 10),
                new(206, 1, "antigravity.exe", "antigravity.exe --crashpad-handler", 1000, 10)
            }
        };

        var aggregator = new HqStateAggregator();
        var collector = new ProcessAgentCollector(aggregator, enabled: true, inspector: mock);

        var detected = collector.DetectProcesses();

        Assert.Equal(4, detected.Count);
        Assert.Contains(detected, a => a.Name == "Claude" && AgentAppearanceRegistry.IsRandomizableSprite(a.SpriteKey) && a.Role == AgentRole.Coder);
        Assert.Contains(detected, a => a.Name == "Codex" && AgentAppearanceRegistry.IsRandomizableSprite(a.SpriteKey) && a.Role == AgentRole.Coder);
        Assert.Contains(detected, a => a.Name == "OpenCode" && AgentAppearanceRegistry.IsRandomizableSprite(a.SpriteKey) && a.Role == AgentRole.External);
        Assert.Contains(detected, a => a.Name == "Gemini" && AgentAppearanceRegistry.IsRandomizableSprite(a.SpriteKey) && a.Role == AgentRole.External);
        Assert.Equal(4, detected.Select(a => a.SpriteKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void DetectProcesses_ActiveChildTools_DetectsGitAndBuild()
    {
        var mock = new MockProcessInspector
        {
            Processes = new List<ProcessItem>
            {
                new(301, 1, "omp.exe", "omp.exe", 1000, 10),
                new(302, 301, "git.exe", "git.exe commit -m \"test\"", 1050, 5)
            }
        };

        var aggregator = new HqStateAggregator();
        var collector = new ProcessAgentCollector(aggregator, enabled: true, inspector: mock);

        var detected = collector.DetectProcesses();

        var agent = Assert.Single(detected);
        Assert.Equal(AgentState.Working, agent.State);
        Assert.Equal("git", agent.ToolName);

        // Change child to dotnet.exe
        mock.Processes = new List<ProcessItem>
        {
            new(301, 1, "omp.exe", "omp.exe", 1000, 15),
            new(303, 301, "dotnet.exe", "dotnet.exe build", 1100, 10)
        };

        detected = collector.DetectProcesses();
        agent = Assert.Single(detected);
        Assert.Equal(AgentState.Working, agent.State);
        Assert.Equal("build", agent.ToolName);
    }

    [Fact]
    public void DetectProcesses_CpuSpike_Hysteresis_AndDoneTransition()
    {
        var time = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var mock = new MockProcessInspector();

        var aggregator = new HqStateAggregator();
        var collector = new ProcessAgentCollector(aggregator, enabled: true, inspector: mock, timeProvider: () => time);

        // Poll 1 (t=0s): baseline first sample -> Idle
        mock.Processes = new List<ProcessItem>
        {
            new(401, 1, "omp.exe", "omp.exe", 1000, 10)
        };
        var detected = collector.DetectProcesses();
        Assert.Equal(AgentState.Idle, detected[0].State);

        // Poll 2 (t=2.5s): CPU spike (delta = 80ms) -> Working ("thinking")
        time = time.AddSeconds(2.5);
        mock.Processes = new List<ProcessItem>
        {
            new(401, 1, "omp.exe", "omp.exe", 1000, 90)
        };
        detected = collector.DetectProcesses();
        Assert.Equal(AgentState.Working, detected[0].State);
        Assert.Equal("thinking", detected[0].ToolName);

        // Keep working for another 2.5s (total working duration = 2.5s >= 2.0s)
        time = time.AddSeconds(2.5);
        mock.Processes = new List<ProcessItem>
        {
            new(401, 1, "omp.exe", "omp.exe", 1000, 180)
        };
        detected = collector.DetectProcesses();
        Assert.Equal(AgentState.Working, detected[0].State);

        // Poll 3 (t=7.5s): Quiet poll 1 (delta = 1ms) -> 1-poll hysteresis holds Working
        time = time.AddSeconds(2.5);
        mock.Processes = new List<ProcessItem>
        {
            new(401, 1, "omp.exe", "omp.exe", 1000, 181)
        };
        detected = collector.DetectProcesses();
        Assert.Equal(AgentState.Working, detected[0].State);

        // Poll 4 (t=10s): Quiet poll 2 (delta = 1ms) -> was working >= 2.0s -> transitions to Done!
        time = time.AddSeconds(2.5);
        mock.Processes = new List<ProcessItem>
        {
            new(401, 1, "omp.exe", "omp.exe", 1000, 182)
        };
        detected = collector.DetectProcesses();
        Assert.Equal(AgentState.Done, detected[0].State);
    }

    [Fact]
    public void DetectProcesses_IdleOmpWithPersistentIdleChildren_ReportsIdleNotWorking()
    {
        var time = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var mock = new MockProcessInspector
        {
            Processes = new List<ProcessItem>
            {
                new(500, 10, "omp.exe", "omp.exe", 1000, 100, HasIoCounters: true, ReadTransferBytes: 50_000, OtherTransferBytes: 20_000),
                // Console host & long-lived idle MCP server child & background omp worker daemon
                new(501, 500, "conhost.exe", "conhost.exe 0x4", 1005, 10),
                new(502, 500, "OpenConsole.exe", "OpenConsole.exe", 1006, 15),
                new(503, 500, "node.exe", "node.exe C:\\mcp\\filesystem-mcp-server.js", 1010, 40),
                new(504, 500, "node.exe", "node.exe C:\\scripts\\persistent-helper.js", 1015, 25),
                new(505, 500, "omp.exe", "omp.exe --__omp_worker_daemon", 1020, 80)
            }
        };

        var aggregator = new HqStateAggregator();
        var collector = new ProcessAgentCollector(aggregator, enabled: true, inspector: mock, timeProvider: () => time);

        // Poll 1: initial baseline
        var poll1 = collector.DetectProcesses();
        var ag1 = Assert.Single(poll1);
        Assert.Equal(AgentState.Idle, ag1.State);
        Assert.Null(ag1.ToolName);
        Assert.Null(ag1.WorkStartedAt);

        // Poll 2 & 3: persistent children sit at 0% CPU while __omp_worker_daemon ticks slightly
        for (int p = 0; p < 3; p++)
        {
            time = time.AddSeconds(2.5);
            mock.Processes = new List<ProcessItem>
            {
                new(500, 10, "omp.exe", "omp.exe", 1000, 100 + (p + 1) * 5, HasIoCounters: true, ReadTransferBytes: 50_000, OtherTransferBytes: 20_000 + (p + 1) * 150),
                new(501, 500, "conhost.exe", "conhost.exe 0x4", 1005, 10),
                new(502, 500, "OpenConsole.exe", "OpenConsole.exe", 1006, 15),
                new(503, 500, "node.exe", "node.exe C:\\mcp\\filesystem-mcp-server.js", 1010, 40),
                new(504, 500, "node.exe", "node.exe C:\\scripts\\persistent-helper.js", 1015, 25),
                new(505, 500, "omp.exe", "omp.exe --__omp_worker_daemon", 1020, 80 + (p + 1) * 90)
            };

            var next = collector.DetectProcesses();
            var ag = Assert.Single(next);
            Assert.Equal(AgentState.Idle, ag.State);
            Assert.Null(ag.ToolName);
            Assert.Null(ag.WorkStartedAt);
        }
    }

    [Fact]
    public void DetectProcesses_IdleOmpWithTuiRedrawJitter_StaysIdleAndTransitionsCleanlyAfterRealWork()
    {
        var time = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var mock = new MockProcessInspector();
        var aggregator = new HqStateAggregator();
        var collector = new ProcessAgentCollector(aggregator, enabled: true, inspector: mock, timeProvider: () => time);

        long cpu = 1000;
        long readBytes = 500_000;
        long otherBytes = 100_000;

        // Poll 1: baseline
        mock.Processes = new List<ProcessItem>
        {
            new(600, 10, "omp.exe", "omp.exe", 1000, cpu, HasIoCounters: true, ReadTransferBytes: readBytes, OtherTransferBytes: otherBytes)
        };
        Assert.Equal(AgentState.Idle, collector.DetectProcesses()[0].State);

        // Polls 2..5: Realistic live Bun/OMP idle TUI (steady ~220ms CPU per 2s redraw loop, 0 read bytes, ~180 B/s console ioctl otherBytes) -> MUST stay Idle!
        for (int i = 0; i < 4; i++)
        {
            time = time.AddSeconds(2.0);
            cpu += 220;
            otherBytes += 280; // 140 B/s (below 512 B/s real socket/IO threshold)
            mock.Processes = new List<ProcessItem>
            {
                new(600, 10, "omp.exe", "omp.exe", 1000, cpu, HasIoCounters: true, ReadTransferBytes: readBytes, OtherTransferBytes: otherBytes)
            };
            var idleRes = collector.DetectProcesses()[0];
            Assert.Equal(AgentState.Idle, idleRes.State);
            Assert.Null(idleRes.WorkStartedAt);
            Assert.Null(idleRes.ToolName);
        }

        // Polls 6 & 7: Real LLM response streaming (CPU + HTTPS socket OtherTransferBytes += 12KB) -> MUST report Working ("thinking")!
        for (int i = 0; i < 2; i++)
        {
            time = time.AddSeconds(2.0);
            cpu += 260;
            otherBytes += 12_000;
            mock.Processes = new List<ProcessItem>
            {
                new(600, 10, "omp.exe", "omp.exe", 1000, cpu, HasIoCounters: true, ReadTransferBytes: readBytes, OtherTransferBytes: otherBytes)
            };
            var workRes = collector.DetectProcesses()[0];
            Assert.Equal(AgentState.Working, workRes.State);
            Assert.Equal("thinking", workRes.ToolName);
            Assert.NotNull(workRes.WorkStartedAt);
        }

        // Poll 8: Quiet poll 1 (back to idle TUI redraw) -> 1-poll hysteresis holds Working
        time = time.AddSeconds(2.0);
        cpu += 220;
        otherBytes += 200;
        mock.Processes = new List<ProcessItem>
        {
            new(600, 10, "omp.exe", "omp.exe", 1000, cpu, HasIoCounters: true, ReadTransferBytes: readBytes, OtherTransferBytes: otherBytes)
        };
        Assert.Equal(AgentState.Working, collector.DetectProcesses()[0].State);

        // Poll 9: Quiet poll 2 -> transitions cleanly to Done (clearing WorkStartedAt & ToolName)
        time = time.AddSeconds(2.0);
        cpu += 220;
        otherBytes += 200;
        mock.Processes = new List<ProcessItem>
        {
            new(600, 10, "omp.exe", "omp.exe", 1000, cpu, HasIoCounters: true, ReadTransferBytes: readBytes, OtherTransferBytes: otherBytes)
        };
        var doneRes = collector.DetectProcesses()[0];
        Assert.Equal(AgentState.Done, doneRes.State);
        Assert.Null(doneRes.WorkStartedAt);
        Assert.Null(doneRes.ToolName);

        // Poll 10: After celebration window (> 5s) -> transitions to Idle (resting!)
        time = time.AddSeconds(5.5);
        cpu += 500;
        otherBytes += 500;
        mock.Processes = new List<ProcessItem>
        {
            new(600, 10, "omp.exe", "omp.exe", 1000, cpu, HasIoCounters: true, ReadTransferBytes: readBytes, OtherTransferBytes: otherBytes)
        };
        var restedRes = collector.DetectProcesses()[0];
        Assert.Equal(AgentState.Idle, restedRes.State);
        Assert.Null(restedRes.WorkStartedAt);
        Assert.Null(restedRes.ToolName);
    }

    [Fact]
    public void DetectProcesses_ActiveOmpWithTransientOrCpuConsumingChild_ReportsWorkingWithAccurateTool()
    {
        var time = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var mock = new MockProcessInspector
        {
            Processes = new List<ProcessItem>
            {
                new(700, 10, "omp.exe", "omp.exe", 1000, 50, HasIoCounters: true, ReadTransferBytes: 10_000, OtherTransferBytes: 5_000),
                new(701, 700, "node.exe", "node.exe persistent-shell.js", 1005, 10)
            }
        };

        var aggregator = new HqStateAggregator();
        var collector = new ProcessAgentCollector(aggregator, enabled: true, inspector: mock, timeProvider: () => time);

        // Baseline: persistent node child at 0% CPU -> Idle
        Assert.Equal(AgentState.Idle, collector.DetectProcesses()[0].State);

        // Spawn transient rg.exe -> Working ("search")
        time = time.AddSeconds(2.0);
        mock.Processes = new List<ProcessItem>
        {
            new(700, 10, "omp.exe", "omp.exe", 1000, 60, HasIoCounters: true, ReadTransferBytes: 10_000, OtherTransferBytes: 5_100),
            new(701, 700, "node.exe", "node.exe persistent-shell.js", 1005, 10),
            new(702, 700, "rg.exe", "rg.exe DrawCharacter src/", 2000, 15)
        };
        var rgRes = collector.DetectProcesses()[0];
        Assert.Equal(AgentState.Working, rgRes.State);
        Assert.Equal("search", rgRes.ToolName);

        // Persistent node child suddenly burns real CPU (delta = 150ms in 2s = 7.5%) -> Working ("execute")
        time = time.AddSeconds(2.0);
        mock.Processes = new List<ProcessItem>
        {
            new(700, 10, "omp.exe", "omp.exe", 1000, 70, HasIoCounters: true, ReadTransferBytes: 10_000, OtherTransferBytes: 5_200),
            new(701, 700, "node.exe", "node.exe persistent-shell.js", 1005, 160)
        };
        var nodeRes = collector.DetectProcesses()[0];
        Assert.Equal(AgentState.Working, nodeRes.State);
        Assert.Equal("execute", nodeRes.ToolName);
    }

    [Fact]
    public void DetectProcesses_MultipleUnnamedSessions_NeverUseCloneNamesAndAssignDistinctStableAppearances()
    {
        var mock = new MockProcessInspector
        {
            Processes = new List<ProcessItem>
            {
                new(1001, 1, "omp.exe", "omp.exe", 1001, 10),
                new(1002, 1, "omp.exe", "omp.exe", 1002, 10),
                new(1003, 1, "omp.exe", "omp.exe", 1003, 10),
                new(1004, 1, "omp.exe", "omp.exe", 1004, 10),
                new(1005, 1, "claude.exe", "claude.exe", 1005, 10),
                new(1006, 1, "claude.exe", "claude.exe", 1006, 10),
                new(1007, 1, "codex.exe", "codex.exe", 1007, 10),
                new(1008, 1, "agy.exe", "agy.exe", 1008, 10)
            }
        };

        var aggregator = new HqStateAggregator();
        var collector = new ProcessAgentCollector(aggregator, enabled: true, inspector: mock);

        var first = collector.DetectProcesses();
        Assert.Equal(8, first.Count);

        // Clean names, zero "Clone" anywhere
        Assert.All(first, a => Assert.DoesNotContain("clone", a.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(first, a => a.Name == "OMP");
        Assert.Contains(first, a => a.Name == "OMP 2");
        Assert.Contains(first, a => a.Name == "OMP 3");
        Assert.Contains(first, a => a.Name == "OMP 4");
        Assert.Contains(first, a => a.Name == "Claude");
        Assert.Contains(first, a => a.Name == "Claude 2");
        Assert.Contains(first, a => a.Name == "Codex");
        Assert.Contains(first, a => a.Name == "AGY");

        // All 8 concurrent sessions receive all 8 distinct characters (zero duplicates until roster exhausted!)
        Assert.Equal(8, first.Select(a => a.SpriteKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(first, a => Assert.True(AgentAppearanceRegistry.IsRandomizableSprite(a.SpriteKey)));
        // All 5 desk rigs are used across the 8 sessions
        Assert.Equal(5, first.Select(a => a.DeskRig).Distinct().Count());
        Assert.All(first, a => Assert.NotEqual(DeskRigKind.Default, a.DeskRig));

        // Stable across subsequent polls
        var second = collector.DetectProcesses();
        for (int i = 0; i < first.Count; i++)
        {
            var match = second.Single(a => a.Id == first[i].Id);
            Assert.Equal(first[i].SpriteKey, match.SpriteKey);
            Assert.Equal(first[i].DeskRig, match.DeskRig);
            Assert.Equal(first[i].BehaviorVariant, match.BehaviorVariant);
        }
    }

    [Theory]
    [InlineData("omp.exe", true)]
    [InlineData("omp", true)]
    [InlineData("agy.exe", true)]
    [InlineData("agy", true)]
    [InlineData("antigravity.exe", true)]
    [InlineData("claude.exe", true)]
    [InlineData("codex.exe", true)]
    [InlineData("opencode.exe", true)]
    [InlineData("gemini.exe", true)]
    [InlineData("node.exe", true)]
    [InlineData("bun.exe", true)]
    [InlineData("git.exe", true)]
    [InlineData("gh.exe", true)]
    [InlineData("dotnet.exe", true)]
    [InlineData("cargo.exe", true)]
    [InlineData("pwsh.exe", true)]
    [InlineData("powershell.exe", true)]
    [InlineData("cmd.exe", true)]
    [InlineData("bash.exe", true)]
    [InlineData("python.exe", true)]
    [InlineData("npm.cmd", true)]
    [InlineData("pnpm.cmd", true)]
    [InlineData("pytest.exe", true)]
    [InlineData("vitest.exe", true)]
    [InlineData("vstest.console.exe", true)]
    [InlineData("chrome.exe", false)]
    [InlineData("msedge.exe", false)]
    [InlineData("svchost.exe", false)]
    [InlineData("explorer.exe", false)]
    [InlineData("1password.exe", false)]
    [InlineData("slack.exe", false)]
    [InlineData("discord.exe", false)]
    [InlineData("spotify.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Win32ProcessInspector_IsRelevantExecutable_CorrectlyClassifies(string? exeName, bool expected)
    {
        bool actual = Win32ProcessInspector.IsRelevantExecutable(exeName!);
        Assert.Equal(expected, actual);
    }

    private class BlockingMockInspector : IProcessInspectionProvider
    {
        public ManualResetEventSlim StartedEvent { get; } = new(false);
        public ManualResetEventSlim UnblockEvent { get; } = new(false);
        public bool CacheCleared { get; private set; }
        public List<ProcessItem> Processes { get; set; } = new();

        public IReadOnlyList<ProcessItem> InspectProcesses()
        {
            StartedEvent.Set();
            UnblockEvent.Wait(5000);
            return Processes;
        }

        public void ClearCache()
        {
            CacheCleared = true;
        }
    }

    [Fact]
    public void Disable_DuringBlockedInspection_VerifiesHistoryTokensAndInspectorCacheStayEmpty()
    {
        var blockingInspector = new BlockingMockInspector
        {
            Processes = new List<ProcessItem>
            {
                new(100, 10, "omp.exe", "omp.exe", 1000, 20)
            }
        };

        var aggregator = new HqStateAggregator();
        var collector = new ProcessAgentCollector(aggregator, enabled: true, inspector: blockingInspector);

        IReadOnlyList<AgentSnapshot>? detected = null;
        var thread = new Thread(() =>
        {
            detected = collector.DetectProcesses();
        });
        thread.Start();

        // Wait until inspection has started and is blocked
        Assert.True(blockingInspector.StartedEvent.Wait(2000));

        // Disable collector while inspection is still in flight
        collector.Enabled = false;

        // Unblock inspection
        blockingInspector.UnblockEvent.Set();
        thread.Join(2000);

        Assert.NotNull(detected);
        Assert.Empty(detected);
        Assert.Equal(0, collector.HistoryCount);
        Assert.Equal(0, collector.OpaqueTokensCount);
        Assert.True(blockingInspector.CacheCleared);
    }

    [Fact]
    public void CollectOnce_RaceEnabledFalseAtPublication_RejectsStalePublishAndPublishesWhenReEnabled()
    {
        var aggregator = new HqStateAggregator();
        var mockInspector = new MockProcessInspector
        {
            Processes = new List<ProcessItem>
            {
                new(100, 10, "claude.exe", "claude.exe", 1000, 20)
            }
        };

        var collector = new ProcessAgentCollector(aggregator, enabled: true, inspector: mockInspector);

        var publicationPaused = new ManualResetEventSlim(false);
        var publicationContinue = new ManualResetEventSlim(false);
        var emittedStates = new List<HqState>();
        aggregator.StateChanged += (_, state) =>
        {
            emittedStates.Add(state);
        };

        collector.BeforePublishAction = () =>
        {
            publicationPaused.Set();
            publicationContinue.Wait(5000);
        };

        // Run CollectOnce on background thread
        var thread = new Thread(() =>
        {
            collector.CollectOnce();
        });
        thread.Start();

        // Wait until CollectOnce finished DetectProcesses and is paused right before UpdateProcessAgents
        Assert.True(publicationPaused.Wait(2000));

        // Disable collector while publication is pending
        collector.Enabled = false;

        // Verify aggregator state has 0 agents and capture emitted states count
        Assert.Empty(aggregator.GetState().Agents);
        int emittedCountWhileDisabled = emittedStates.Count;

        // Unblock publication
        publicationContinue.Set();
        thread.Join(2000);

        // Verify aggregator state stays 0 agents and no stale process agent was published
        Assert.Empty(aggregator.GetState().Agents);
        Assert.Equal(emittedCountWhileDisabled, emittedStates.Count);

        // Re-enable collector and collect once
        collector.BeforePublishAction = null;
        collector.Enabled = true;
        collector.CollectOnce();

        // Verify re-enabling publishes normally under the new generation
        var finalState = aggregator.GetState();
        Assert.Single(finalState.Agents);
        Assert.Equal("Claude", finalState.Agents[0].Name);
        Assert.Equal("process", finalState.Agents[0].Source);
    }
}
