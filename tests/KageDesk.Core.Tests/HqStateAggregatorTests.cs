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

    [Fact]
    public void FreshCiStormLogic_NewerSuccessClearsOlderFailure_ForSameRepo()
    {
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        var baseDate = new DateTimeOffset(2026, 11, 25, 12, 0, 0, localOffset);

        // Older failing build (10:00) and newer successful build (10:05) for same repo
        var builds = new[]
        {
            new BuildInfo("b-old", "kagedesk", "CI", "main", "Older Run", "completed", "failure", UpdatedAt: baseDate.AddMinutes(-5)),
            new BuildInfo("b-new", "kagedesk", "CI", "main", "Newer Run", "completed", "success", UpdatedAt: baseDate)
        };

        // StormAlert should NOT be triggered because the latest run is green
        var weather = HqStateAggregator.ComputeEffectiveWeather(WeatherMode.Auto, builds, baseDate);
        Assert.Equal(EffectiveWeather.Day, weather);

        // Mood should be Celebrating or Calm, definitely NOT Failing
        var mood = HqStateAggregator.ComputeHqMood(Array.Empty<AgentSnapshot>(), builds, Array.Empty<HqEvent>());
        Assert.NotEqual(HqMood.Failing, mood);
    }

    [Fact]
    public void OrcaAccountPruning_RemovedAccountsArePrunedOnReplacement()
    {
        var aggregator = new HqStateAggregator();

        var initialAccounts = new[]
        {
            new AccountInfo("orca", "acc-1", "Account 1", true),
            new AccountInfo("orca", "acc-2", "Account 2", true)
        };
        aggregator.UpdateOrcaSnapshot(Array.Empty<AgentSnapshot>(), initialAccounts);
        Assert.Equal(2, aggregator.GetState().Accounts.Count);

        // Next snapshot: acc-2 was deleted/logged out
        var updatedAccounts = new[]
        {
            new AccountInfo("orca", "acc-1", "Account 1", true)
        };
        aggregator.UpdateOrcaSnapshot(Array.Empty<AgentSnapshot>(), updatedAccounts);

        var accounts = aggregator.GetState().Accounts;
        Assert.Single(accounts);
        Assert.Equal("acc-1", accounts[0].AccountId);
    }

    [Fact]
    public void DepartedDoneAgent_CelebratedDuringWindow_ThenPrunedCompletely()
    {
        var now = DateTimeOffset.UtcNow;
        var currentTime = now;
        var config = new AppConfig { CelebrationDurationSeconds = 10 };
        var aggregator = new HqStateAggregator(config, () => currentTime);

        // Step 1: Agent reports Done in live Orca snapshot
        var doneAgent = new AgentSnapshot("done-ag", "Done Worker", AgentRole.Coder, "minato", AgentState.Done, UpdatedAt: currentTime);
        aggregator.UpdateOrcaSnapshot(new[] { doneAgent }, Array.Empty<AccountInfo>());
        Assert.Single(aggregator.GetState().Agents);
        Assert.Equal(AgentState.Done, aggregator.GetState().Agents[0].State);

        // Step 2: In next poll, agent has departed (no longer in live snapshot)
        // Time is +4s (within 10s celebration window)
        currentTime = now.AddSeconds(4);
        aggregator.UpdateOrcaSnapshot(Array.Empty<AgentSnapshot>(), Array.Empty<AccountInfo>());

        var midState = aggregator.GetState();
        Assert.Single(midState.Agents);
        Assert.Equal("done-ag", midState.Agents[0].Id);
        Assert.Equal(AgentState.Done, midState.Agents[0].State);

        // Step 3: Time advances beyond celebration window (+12s)
        currentTime = now.AddSeconds(12);
        aggregator.Reevaluate();

        var postState = aggregator.GetState();
        // Agent must be completely PRUNED (not relaxed to Idle!)
        Assert.Empty(postState.Agents);
    }

    [Fact]
    public void CollectorDisable_ClearsCollectorOwnedEntries()
    {
        var aggregator = new HqStateAggregator();

        aggregator.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("orca-1", "Orca Agent", AgentRole.Leader, "shikamaru", AgentState.Working)
        }, new[]
        {
            new AccountInfo("orca", "acc-orca", "Orca Acc", true)
        });

        aggregator.UpdateProcessAgents(new[]
        {
            new AgentSnapshot("proc-1", "Proc Agent", AgentRole.Coder, "minato", AgentState.Working)
        });

        aggregator.UpdateCiBuilds(new[]
        {
            new BuildInfo("ci-1", "kagedesk", "CI", "main", "Build", "completed", "success")
        });

        Assert.Equal(2, aggregator.GetState().Agents.Count);
        Assert.Single(aggregator.GetState().Accounts);
        Assert.Single(aggregator.GetState().Builds);

        // Clear Orca
        aggregator.ClearOrcaEntries();
        Assert.Single(aggregator.GetState().Agents);
        Assert.Equal("proc-1", aggregator.GetState().Agents[0].Id);
        Assert.Empty(aggregator.GetState().Accounts);

        // Clear Proc
        aggregator.ClearProcessAgentEntries();
        Assert.Empty(aggregator.GetState().Agents);

        // Clear CI
        aggregator.ClearCiBuildEntries();
        Assert.Empty(aggregator.GetState().Builds);
    }

    [Fact]
    public void StateChanged_FiredOutsideLock_CanSafelyQueryStateWithoutDeadlock()
    {
        var aggregator = new HqStateAggregator();
        bool received = false;

        aggregator.StateChanged += (sender, state) =>
        {
            // Querying aggregator inside event handler must not deadlock
            var queried = aggregator.GetState();
            Assert.NotNull(queried);
            received = true;
        };

        aggregator.SetDemoScenario("working");
        Assert.True(received);
    }

    [Fact]
    public void UpdateCiBuildsForRepo_EmptyBuilds_ClearsRepoRunsAndStormAlert()
    {
        var aggregator = new HqStateAggregator();
        var repo = "kagedesk-test/repo-1";

        // Initial failing build triggers StormAlert
        aggregator.UpdateCiBuildsForRepo(repo, new[]
        {
            new BuildInfo("b-1", repo, "CI", "main", "Build Failed", "completed", "failure")
        });

        var stateWithFailure = aggregator.GetState();
        Assert.Single(stateWithFailure.Builds);
        Assert.Equal(EffectiveWeather.StormAlert, stateWithFailure.Weather);

        // Empty run list reported for this repo
        aggregator.UpdateCiBuildsForRepo(repo, Array.Empty<BuildInfo>());

        var stateCleared = aggregator.GetState();
        Assert.Empty(stateCleared.Builds);
        Assert.NotEqual(EffectiveWeather.StormAlert, stateCleared.Weather);
    }

    [Fact]
    public void OrderRunsNewestFirst_OrdersByCreatedAt_BeforeUpdatedAt()
    {
        var aggregator = new HqStateAggregator();
        var repo = "kagedesk-test/repo-order";
        var t0 = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

        // Older run (created 10:00, failed, updated later at 10:30)
        var olderRun = new BuildInfo("b-old", repo, "CI", "main", "Older", "completed", "failure",
            CreatedAt: t0, UpdatedAt: t0.AddMinutes(30));

        // Newer run (created 10:20, succeeded, updated at 10:25)
        var newerRun = new BuildInfo("b-new", repo, "CI", "main", "Newer", "completed", "success",
            CreatedAt: t0.AddMinutes(20), UpdatedAt: t0.AddMinutes(25));

        aggregator.UpdateCiBuilds(new[] { olderRun, newerRun });

        var state = aggregator.GetState();
        Assert.Equal(2, state.Builds.Count);
        Assert.Equal("b-new", state.Builds[0].Id);
        Assert.Equal("success", state.Builds[0].Conclusion);
        Assert.NotEqual(EffectiveWeather.StormAlert, state.Weather);
    }

    [Fact]
    public void PushedAccounts_CappedAt32_DoesNotEvictOrcaAccounts()
    {
        var aggregator = new HqStateAggregator();

        // 1. Snapshot with Orca accounts
        aggregator.UpdateOrcaSnapshot(Array.Empty<AgentSnapshot>(), new[]
        {
            new AccountInfo("orca", "orca-acc-1", "Orca Account", true)
        });

        Assert.Single(aggregator.GetState().Accounts);

        // 2. Push 35 bridge accounts
        for (int i = 0; i < 35; i++)
        {
            aggregator.UpdatePushedEvent(new PushedEventPayload(
                AccountProvider: "bridge",
                AccountName: $"acc-{i}",
                QuotaPercent: 50.0
            ));
        }

        var accounts = aggregator.GetState().Accounts;
        // Orca account is NEVER evicted by pushed bridge accounts
        Assert.Contains(accounts, a => a.Provider == "orca" && a.AccountId == "orca-acc-1");

        // Pushed accounts must be capped to at most 32
        var pushedCount = accounts.Count(a => a.Provider == "bridge");
        Assert.True(pushedCount <= 32, $"Expected at most 32 pushed accounts, got {pushedCount}");
    }

    [Fact]
    public void SerializedCiGeneration_RejectsStalePublication_NoStormAlertOrVisualEvent()
    {
        var aggregator = new HqStateAggregator();
        long gen = aggregator.GetCiGeneration();

        // Simulate disabling CI: clears builds and bumps generation
        long disabledGen = aggregator.DisableAndClearCiBuilds();
        Assert.True(disabledGen > gen);

        // Attempt in-flight publication from the captured earlier generation
        var failingRuns = new[]
        {
            new BuildInfo("b-fail-1", "test/repo", "CI", "main", "CI", "completed", "failure")
        };

        bool accepted = aggregator.UpdateCiBuildsForRepo("test/repo", failingRuns, expectedGeneration: gen);

        Assert.False(accepted);
        var state = aggregator.GetState();
        Assert.Empty(state.Builds);
        Assert.NotEqual(EffectiveWeather.StormAlert, state.Weather);
        Assert.DoesNotContain(state.PendingVisualEvents, e => e.Type == "BuildFailed");
    }
}

