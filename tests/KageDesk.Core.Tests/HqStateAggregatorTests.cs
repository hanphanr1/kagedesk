using KageDesk.Core.Models;
using KageDesk.Core.State;
using Xunit;

namespace KageDesk.Core.Tests;

public class HqStateAggregatorTests
{
    [Fact]
    public void StateChanged_SuppressedOnNoOpUpdate()
    {
        var aggregator = new HqStateAggregator();
        int eventCount = 0;
        aggregator.StateChanged += (_, _) => eventCount++;

        var agents = new List<AgentSnapshot>
        {
            new("ag-1", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working)
        };
        var accounts = new List<AccountInfo>
        {
            new("Claude", "sys", "System", true)
        };

        // First update -> triggers event
        aggregator.UpdateOrcaSnapshot(agents, accounts);
        Assert.Equal(1, eventCount);

        // Identical second update -> suppressed
        aggregator.UpdateOrcaSnapshot(agents, accounts);
        Assert.Equal(1, eventCount);

        // State change -> triggers event
        var updatedAgents = new List<AgentSnapshot>
        {
            new("ag-1", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle)
        };
        aggregator.UpdateOrcaSnapshot(updatedAgents, accounts);
        Assert.Equal(2, eventCount);
    }

    [Fact]
    public void ThreadSafety_ConcurrentUpdates_ProducesValidState()
    {
        var aggregator = new HqStateAggregator();
        var exceptions = new List<Exception>();

        Parallel.For(0, 100, i =>
        {
            try
            {
                if (i % 3 == 0)
                {
                    aggregator.UpdatePushedEvent(new PushedEventPayload(
                        AgentId: $"agent-{i}",
                        Name: $"Agent #{i}",
                        State: AgentState.Working
                    ));
                }
                else if (i % 3 == 1)
                {
                    aggregator.UpdateCiBuilds(new[]
                    {
                        new BuildInfo($"build-{i}", "kagedesk", "CI", "main", $"Run {i}", "completed", i % 2 == 0 ? "success" : "failure")
                    });
                }
                else
                {
                    _ = aggregator.GetState();
                }
            }
            catch (Exception ex)
            {
                lock (exceptions) exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
        var finalState = aggregator.GetState();
        Assert.NotNull(finalState);
    }

    [Fact]
    public void HqMood_PriorityRules_EvaluatedCorrectly()
    {
        var now = DateTimeOffset.UtcNow;
        var emptyEvents = Array.Empty<HqEvent>();

        // 1. Alert (Approval) overrides Failing
        var agentsWithApproval = new[]
        {
            new AgentSnapshot("1", "A", AgentRole.Leader, "shikamaru", AgentState.NeedsApproval),
            new AgentSnapshot("2", "B", AgentRole.Coder, "minato", AgentState.Failed)
        };
        var failingBuilds = new[]
        {
            new BuildInfo("b1", "repo", "CI", "main", "Build", "completed", "failure")
        };
        Assert.Equal(HqMood.Alert, HqStateAggregator.ComputeHqMood(agentsWithApproval, failingBuilds, emptyEvents));

        // 2. Failing overrides Busy
        var agentsWithWorking = new[]
        {
            new AgentSnapshot("2", "B", AgentRole.Coder, "minato", AgentState.Working)
        };
        Assert.Equal(HqMood.Failing, HqStateAggregator.ComputeHqMood(agentsWithWorking, failingBuilds, emptyEvents));

        // 3. Busy overrides Celebrating
        var agentsWorkingAndDone = new[]
        {
            new AgentSnapshot("1", "A", AgentRole.Leader, "shikamaru", AgentState.Working),
            new AgentSnapshot("2", "B", AgentRole.Coder, "minato", AgentState.Done)
        };
        Assert.Equal(HqMood.Busy, HqStateAggregator.ComputeHqMood(agentsWorkingAndDone, Array.Empty<BuildInfo>(), emptyEvents));

        // 4. Celebrating overrides Calm
        var agentsDone = new[]
        {
            new AgentSnapshot("1", "A", AgentRole.Leader, "shikamaru", AgentState.Done)
        };
        Assert.Equal(HqMood.Celebrating, HqStateAggregator.ComputeHqMood(agentsDone, Array.Empty<BuildInfo>(), emptyEvents));

        // 5. Calm when all idle
        var agentsIdle = new[]
        {
            new AgentSnapshot("1", "A", AgentRole.Leader, "shikamaru", AgentState.Idle)
        };
        Assert.Equal(HqMood.Calm, HqStateAggregator.ComputeHqMood(agentsIdle, Array.Empty<BuildInfo>(), emptyEvents));
    }

    [Fact]
    public void VisualEvents_CloneTransitions_GeneratesMonotonicSequenceIds()
    {
        var aggregator = new HqStateAggregator();

        // 1. Initial leader agent
        aggregator.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, IsBotHome: true)
        }, Array.Empty<AccountInfo>());

        var state1 = aggregator.GetState();
        Assert.Empty(state1.PendingVisualEvents);

        // 2. Clone appears
        aggregator.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, IsBotHome: true),
            new AgentSnapshot("clone-1", "Clone 1", AgentRole.Clone, "clone", AgentState.Working, IsBotHome: false)
        }, Array.Empty<AccountInfo>());

        var state2 = aggregator.GetState();
        var spawnEvent = state2.PendingVisualEvents.FirstOrDefault(e => e.Type == "CloneSpawned");
        Assert.NotNull(spawnEvent);
        Assert.Equal("clone-1", spawnEvent.AgentId);

        // 3. Clone despawns
        aggregator.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, IsBotHome: true)
        }, Array.Empty<AccountInfo>());

        var state3 = aggregator.GetState();
        var despawnEvent = state3.PendingVisualEvents.FirstOrDefault(e => e.Type == "CloneDespawned");
        Assert.NotNull(despawnEvent);
        Assert.Equal("clone-1", despawnEvent.AgentId);
        Assert.True(despawnEvent.SequenceId > spawnEvent.SequenceId, "SequenceId must be monotonically increasing");
    }

    [Fact]
    public void VisualEvents_BuildPassAndFail_EmitsEvents()
    {
        var aggregator = new HqStateAggregator();

        // 1. Failing build
        aggregator.UpdateCiBuilds(new[]
        {
            new BuildInfo("b-1", "kagedesk", "CI", "main", "Build #1", "completed", "failure")
        });

        var state1 = aggregator.GetState();
        var failEvent = state1.PendingVisualEvents.FirstOrDefault(e => e.Type == "BuildFailed");
        Assert.NotNull(failEvent);

        // 2. Passing build
        aggregator.UpdateCiBuilds(new[]
        {
            new BuildInfo("b-1", "kagedesk", "CI", "main", "Build #2", "completed", "success")
        });

        var state2 = aggregator.GetState();
        var passEvent = state2.PendingVisualEvents.FirstOrDefault(e => e.Type == "BuildPassed");
        Assert.NotNull(passEvent);
        Assert.True(passEvent.SequenceId > failEvent.SequenceId);
    }

    [Fact]
    public void CelebrationWindow_RelaxesDoneToIdleAfter6Seconds()
    {
        var currentTime = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var aggregator = new HqStateAggregator(
            config: new AppConfig { CelebrationDurationSeconds = 6.0 },
            timeProvider: () => currentTime
        );

        // Agent completes task and is in Done state
        aggregator.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("ag-1", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Done, UpdatedAt: currentTime)
        }, Array.Empty<AccountInfo>());

        // Within celebration window (3 seconds later) -> still Done
        currentTime = currentTime.AddSeconds(3);
        aggregator.Reevaluate();
        var stateDuring = aggregator.GetState();
        Assert.Equal(AgentState.Done, stateDuring.Agents[0].State);

        // Beyond celebration window (7 seconds later from start) -> relaxed to Idle
        currentTime = currentTime.AddSeconds(4); // total 7s
        aggregator.Reevaluate();
        var stateAfter = aggregator.GetState();
        Assert.Equal(AgentState.Idle, stateAfter.Agents[0].State);
    }

    [Fact]
    public void DemoScenario_OverridesAndRestoresLiveState()
    {
        var aggregator = new HqStateAggregator();

        // Push live agent
        aggregator.UpdatePushedEvent(new PushedEventPayload(AgentId: "live-1", Name: "Live Agent", State: AgentState.Working));
        Assert.Single(aggregator.GetState().Agents);
        Assert.Equal("live-1", aggregator.GetState().Agents[0].Id);

        // Override with demo "approve"
        aggregator.SetDemoScenario("approve");
        var demoState = aggregator.GetState();
        Assert.Equal("approve", demoState.DemoScenario);
        Assert.Contains(demoState.Agents, a => a.State == AgentState.NeedsApproval);
        Assert.Equal(HqMood.Alert, demoState.Mood);

        // Clear demo -> restores live data
        aggregator.SetDemoScenario("clear");
        var restoredState = aggregator.GetState();
        Assert.Null(restoredState.DemoScenario);
        Assert.Single(restoredState.Agents);
        Assert.Equal("live-1", restoredState.Agents[0].Id);

        // Invalid scenario throws ArgumentException
        Assert.Throws<ArgumentException>(() => aggregator.SetDemoScenario("invalid-scenario"));
    }

    [Fact]
    public void EffectiveWeather_TimeOfDayAndStormAlertRules()
    {
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        var baseDate = new DateTimeOffset(2026, 11, 25, 0, 0, 0, localOffset);
        var noBuilds = Array.Empty<BuildInfo>();
        var failingBuilds = new[] { new BuildInfo("b1", "repo", "CI", "main", "CI", "completed", "failure") };

        // Dawn: 5..8
        var dawnTime = baseDate.AddHours(6);
        Assert.Equal(EffectiveWeather.Dawn, HqStateAggregator.ComputeEffectiveWeather(WeatherMode.Auto, noBuilds, dawnTime));

        // Day: 9..16
        var dayTime = baseDate.AddHours(14);
        Assert.Equal(EffectiveWeather.Day, HqStateAggregator.ComputeEffectiveWeather(WeatherMode.Auto, noBuilds, dayTime));

        // Sunset: 17..19
        var sunsetTime = baseDate.AddHours(18);
        Assert.Equal(EffectiveWeather.Sunset, HqStateAggregator.ComputeEffectiveWeather(WeatherMode.Auto, noBuilds, sunsetTime));

        // MidnightRain: 20..04
        var midnightTime = baseDate.AddHours(23);
        Assert.Equal(EffectiveWeather.MidnightRain, HqStateAggregator.ComputeEffectiveWeather(WeatherMode.Auto, noBuilds, midnightTime));

        // Failing build overrides Auto weather to StormAlert
        Assert.Equal(EffectiveWeather.StormAlert, HqStateAggregator.ComputeEffectiveWeather(WeatherMode.Auto, failingBuilds, dayTime));

        // Forced weather mode preserves setting
        Assert.Equal(EffectiveWeather.Sunset, HqStateAggregator.ComputeEffectiveWeather(WeatherMode.Sunset, failingBuilds, dayTime));
    }
}
