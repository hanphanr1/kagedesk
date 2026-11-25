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
        Assert.Equal("proc:omp:100", single.Id);
        Assert.Equal("OMP", single.Name);
        Assert.Equal(AgentRole.Coder, single.Role);
        Assert.Equal("omp", single.SpriteKey);
        Assert.Equal(100, single.ProcessId);
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
        Assert.Equal("OMP #1", detected[0].Name);
        Assert.Equal(100, detected[0].ProcessId);
        Assert.Equal("OMP #2", detected[1].Name);
        Assert.Equal(200, detected[1].ProcessId);
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
        Assert.Equal("proc:agy:81", agent.Id);
        Assert.Equal("AGY", agent.Name);
        Assert.Equal("agy", agent.SpriteKey);
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
        Assert.Contains(detected, a => a.Name == "CLAUDE" && a.SpriteKey == "claude" && a.Role == AgentRole.Coder);
        Assert.Contains(detected, a => a.Name == "CODEX" && a.SpriteKey == "codex" && a.Role == AgentRole.Coder);
        Assert.Contains(detected, a => a.Name == "OPENCODE" && a.SpriteKey == "generic" && a.Role == AgentRole.External);
        Assert.Contains(detected, a => a.Name == "GEMINI" && a.SpriteKey == "agy" && a.Role == AgentRole.External);
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
}
