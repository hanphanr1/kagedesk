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

    [Fact]
    public void SerializedProcessGeneration_RejectsStalePublication_NoStaleAgentsEmitted()
    {
        var aggregator = new HqStateAggregator();
        long gen = aggregator.GetProcessGeneration();

        // Simulate disabling process agents: clears agents and bumps generation
        long disabledGen = aggregator.DisableAndClearProcessAgents();
        Assert.True(disabledGen > gen);

        // Attempt in-flight publication from the captured earlier generation
        var inFlightAgents = new[]
        {
            new AgentSnapshot("proc-stale", "Stale Agent", AgentRole.Coder, "kakashi", AgentState.Working)
        };

        bool accepted = aggregator.UpdateProcessAgents(inFlightAgents, expectedGeneration: gen);

        Assert.False(accepted);
        var state = aggregator.GetState();
        Assert.Empty(state.Agents);
    }

    [Fact]
    public void AutoHandoffDetection_EmitsHandoffVisualEvent_WhenAgentExitsWorkingAndOtherEntersWorkingWithin15Seconds()
    {
        var now = DateTimeOffset.UtcNow;
        var aggregator = new HqStateAggregator(timeProvider: () => now);

        // Step 1: Agent A is Working
        var agentA = new AgentSnapshot("agent-a", "Agent A", AgentRole.Coder, "minato", AgentState.Working);
        aggregator.UpdateOrcaSnapshot(new[] { agentA }, Array.Empty<AccountInfo>());

        var state1 = aggregator.GetState();
        Assert.Single(state1.Agents);
        Assert.NotNull(state1.Agents[0].WorkStartedAt);

        // Step 2: 5 seconds later, Agent A finishes (becomes Done/Idle) and Agent B starts Working
        now = now.AddSeconds(5);
        var agentADone = agentA with { State = AgentState.Done };
        var agentB = new AgentSnapshot("agent-b", "Agent B", AgentRole.Tester, "tsunade", AgentState.Working);
        aggregator.UpdateOrcaSnapshot(new[] { agentADone, agentB }, Array.Empty<AccountInfo>());

        var state2 = aggregator.GetState();
        var handoffEvent = state2.PendingVisualEvents.FirstOrDefault(e => e.Type == "handoff");
        Assert.NotNull(handoffEvent);
        Assert.Equal("agent-a", handoffEvent.AgentId);
        Assert.Equal("agent-b", handoffEvent.TargetAgentId);
    }

    [Fact]
    public void ExplicitHandoffPushedEvent_EmitsHandoffEvent_WithFromAndToAgentIds()
    {
        var aggregator = new HqStateAggregator();
        aggregator.UpdatePushedEvent(new PushedEventPayload(
            Type: "handoff",
            FromAgentId: "minato-1",
            ToAgentId: "tsunade-1",
            Message: "Scroll transfer to QA"
        ));

        var state = aggregator.GetState();
        var ev = state.PendingVisualEvents.FirstOrDefault(e => e.Type == "handoff");
        Assert.NotNull(ev);
        Assert.Equal("minato-1", ev.AgentId);
        Assert.Equal("tsunade-1", ev.TargetAgentId);
        Assert.Equal("Scroll transfer to QA", ev.Message);
    }

    [Fact]
    public void DemoScenario_Handoff_ContainsActiveHandoffEventAndWorkingAgents()
    {
        var aggregator = new HqStateAggregator();
        aggregator.SetDemoScenario("handoff");

        var state = aggregator.GetState();
        Assert.Equal("handoff", state.DemoScenario);
        Assert.Contains(state.Agents, a => a.Id == "demo-coder" && a.State == AgentState.Working && a.WorkStartedAt != null);
        Assert.Contains(state.Agents, a => a.Id == "demo-tester" && a.State == AgentState.Working && a.WorkStartedAt != null);

        var ev = state.PendingVisualEvents.FirstOrDefault(e => e.Type == "handoff");
        Assert.NotNull(ev);
        Assert.Equal("demo-coder", ev.AgentId);
        Assert.Equal("demo-tester", ev.TargetAgentId);
    }

    [Fact]
    public void WorkStartedAt_TrackedAccurately_AndResetToNullWhenReturningToIdle()
    {
        var now = DateTimeOffset.UtcNow;
        var aggregator = new HqStateAggregator(timeProvider: () => now);

        var agent = new AgentSnapshot("ag-1", "Ag 1", AgentRole.Coder, "minato", AgentState.Working);
        aggregator.UpdateOrcaSnapshot(new[] { agent }, Array.Empty<AccountInfo>());

        var stateWorking = aggregator.GetState();
        var startedAt = stateWorking.Agents[0].WorkStartedAt;
        Assert.NotNull(startedAt);
        Assert.Equal(now, startedAt.Value);

        // Advance time and remain working: WorkStartedAt should remain stable
        now = now.AddSeconds(10);
        aggregator.Reevaluate();
        var stateWorking2 = aggregator.GetState();
        Assert.Equal(startedAt, stateWorking2.Agents[0].WorkStartedAt);

        // Transition to Idle: WorkStartedAt must reset to null
        now = now.AddSeconds(5);
        aggregator.UpdateOrcaSnapshot(new[] { agent with { State = AgentState.Idle } }, Array.Empty<AccountInfo>());
        var stateIdle = aggregator.GetState();
        Assert.Null(stateIdle.Agents[0].WorkStartedAt);
    }

    [Fact]
    public void OrcaSnapshotParser_ParsesStateStartedAt_WhenWorkingOrNeedsApproval()
    {
        var now = DateTimeOffset.UtcNow;
        long epochMs = 1732560000000L;
        var expectedDto = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);

        var json = $$"""
        {
            "ok": true,
            "result": {
                "worktrees": [
                    {
                        "worktreeId": "wt-1",
                        "status": "working",
                        "agents": [
                            {
                                "address": "ag-active",
                                "state": "working",
                                "stateStartedAt": {{epochMs}},
                                "taskTitle": "Compiling kernel"
                            }
                        ]
                    }
                ]
            }
        }
        """;

        var agents = KageDesk.Core.Collectors.OrcaSnapshotParser.ParseWorktreePs(json, now);
        Assert.Single(agents);
        Assert.Equal(AgentState.Working, agents[0].State);
        Assert.Equal(expectedDto, agents[0].WorkStartedAt);
    }

    [Fact]
    public void AutoHandoff_DoesNotTrigger_WhenTransitionsAreMoreThan15SecondsApart()
    {
        var now = DateTimeOffset.UtcNow;
        var aggregator = new HqStateAggregator(timeProvider: () => now);

        // Step 1: Agent 1 working
        var ag1 = new AgentSnapshot("ag-1", "Agent 1", AgentRole.Coder, "minato", AgentState.Working);
        var ag2 = new AgentSnapshot("ag-2", "Agent 2", AgentRole.Tester, "tsunade", AgentState.Idle);
        aggregator.UpdateOrcaSnapshot(new[] { ag1, ag2 }, Array.Empty<AccountInfo>());

        // Step 2: Agent 1 finishes working at t = 0
        aggregator.UpdateOrcaSnapshot(new[] { ag1 with { State = AgentState.Done }, ag2 }, Array.Empty<AccountInfo>());

        // Step 3: Advance 16 seconds (> 15s window)
        now = now.AddSeconds(16);

        // Step 4: Agent 2 starts working
        aggregator.UpdateOrcaSnapshot(new[] { ag1 with { State = AgentState.Done }, ag2 with { State = AgentState.Working } }, Array.Empty<AccountInfo>());

        var state = aggregator.GetState();
        var handoffEvent = state.PendingVisualEvents.FirstOrDefault(e => e.Type == "handoff");
        Assert.Null(handoffEvent);
    }

    [Fact]
    public void AutoHandoff_DoesNotTrigger_ForSameAgent()
    {
        var now = DateTimeOffset.UtcNow;
        var aggregator = new HqStateAggregator(timeProvider: () => now);

        var ag1 = new AgentSnapshot("ag-1", "Agent 1", AgentRole.Coder, "minato", AgentState.Working);
        aggregator.UpdateOrcaSnapshot(new[] { ag1 }, Array.Empty<AccountInfo>());

        // Agent 1 finishes at t = 0
        aggregator.UpdateOrcaSnapshot(new[] { ag1 with { State = AgentState.Done } }, Array.Empty<AccountInfo>());

        // Agent 1 starts working again at t = 2s (same agent)
        now = now.AddSeconds(2);
        aggregator.UpdateOrcaSnapshot(new[] { ag1 with { State = AgentState.Working } }, Array.Empty<AccountInfo>());

        var state = aggregator.GetState();
        var handoffEvent = state.PendingVisualEvents.FirstOrDefault(e => e.Type == "handoff");
        Assert.Null(handoffEvent);
    }

    [Fact]
    public void OrcaSnapshotParser_NullWorkStartedAt_WhenAgentIsIdle()
    {
        var now = DateTimeOffset.UtcNow;
        long epochMs = 1732560000000L;

        var json = $$"""
        {
            "ok": true,
            "result": {
                "worktrees": [
                    {
                        "worktreeId": "wt-1",
                        "status": "idle",
                        "agents": [
                            {
                                "address": "ag-idle",
                                "state": "idle",
                                "stateStartedAt": {{epochMs}},
                                "taskTitle": "Idle task"
                            }
                        ]
                    }
                ]
            }
        }
        """;

        var agents = KageDesk.Core.Collectors.OrcaSnapshotParser.ParseWorktreePs(json, now);
        Assert.Single(agents);
        Assert.Equal(AgentState.Idle, agents[0].State);
        Assert.Null(agents[0].WorkStartedAt);
    }

    [Fact]
    public void OrcaSnapshotParser_ParsesMainAgentStateStartedAt_WhenNeedsApproval()
    {
        var now = DateTimeOffset.UtcNow;
        long epochMs = 1732560120000L;
        var expectedDto = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);

        var json = $$"""
        {
            "ok": true,
            "result": {
                "worktrees": [
                    {
                        "worktreeId": "wt-gate",
                        "status": "needs_approval",
                        "mainAgent": {
                            "address": "ag-leader",
                            "state": "needs_approval",
                            "stateStartedAt": {{epochMs}},
                            "taskTitle": "Deployment approval gate"
                        }
                    }
                ]
            }
        }
        """;

        var agents = KageDesk.Core.Collectors.OrcaSnapshotParser.ParseWorktreePs(json, now);
        Assert.Single(agents);
        Assert.Equal(AgentState.NeedsApproval, agents[0].State);
        Assert.Equal(expectedDto, agents[0].WorkStartedAt);
    }

    [Fact]
    public void RecalculateState_ClearsWorkStartedAt_WhenAgentTransitionsToDoneOrFailed()
    {
        var now = DateTimeOffset.UtcNow;
        var agg = new HqStateAggregator(timeProvider: () => now);

        // 1. Agent enters Working
        agg.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("agent-1", "Minato", AgentRole.Coder, "minato", AgentState.Working)
        }, Array.Empty<AccountInfo>());

        var state1 = agg.GetState();
        var ag1 = state1.Agents.First(a => a.Id == "agent-1");
        Assert.NotNull(ag1.WorkStartedAt);
        Assert.Equal(now, ag1.WorkStartedAt.Value);

        // 2. Agent transitions to Done
        var doneTime = now.AddMinutes(5);
        agg = new HqStateAggregator(timeProvider: () => doneTime);
        agg.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("agent-1", "Minato", AgentRole.Coder, "minato", AgentState.Working)
        }, Array.Empty<AccountInfo>());
        agg.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("agent-1", "Minato", AgentRole.Coder, "minato", AgentState.Done, UpdatedAt: doneTime)
        }, Array.Empty<AccountInfo>());

        var state2 = agg.GetState();
        var ag2 = state2.Agents.First(a => a.Id == "agent-1");
        Assert.Equal(AgentState.Done, ag2.State);
        Assert.Null(ag2.WorkStartedAt);

        // 3. Agent starts a new task later: must receive new start time, not old cached time
        var newWorkTime = doneTime.AddMinutes(10);
        var agg3 = new HqStateAggregator(timeProvider: () => newWorkTime);
        agg3.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("agent-1", "Minato", AgentRole.Coder, "minato", AgentState.Done, UpdatedAt: doneTime)
        }, Array.Empty<AccountInfo>());
        agg3.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("agent-1", "Minato", AgentRole.Coder, "minato", AgentState.Working)
        }, Array.Empty<AccountInfo>());

        var state3 = agg3.GetState();
        var ag3 = state3.Agents.First(a => a.Id == "agent-1");
        Assert.Equal(AgentState.Working, ag3.State);
        Assert.Equal(newWorkTime, ag3.WorkStartedAt);
    }

    [Fact]
    public void SetDemoScenario_Handoff_EmitsVisualEventOnlyOnceAcrossEvaluations()
    {
        var now = DateTimeOffset.UtcNow;
        var agg = new HqStateAggregator(timeProvider: () => now);

        agg.SetDemoScenario("handoff");
        var state1 = agg.GetState();
        var handoffEvents1 = state1.PendingVisualEvents.Count(e => e.Type == "handoff");
        Assert.Equal(1, handoffEvents1);

        // Repeated reevaluations
        agg.Reevaluate();
        agg.Reevaluate();
        var state2 = agg.GetState();
        var handoffEvents2 = state2.PendingVisualEvents.Count(e => e.Type == "handoff");
        Assert.Equal(1, handoffEvents2);
    }

    [Fact]
    public void HqState_Equality_IgnoresRevision()
    {
        var s1 = new HqState(
            Agents: Array.Empty<AgentSnapshot>(),
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Calm,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Revision: 1
        );

        var s2 = s1 with { Revision = 999 };

        Assert.True(s1.Equals(s2));
        Assert.True(s2.Equals(s1));
        Assert.True(s1 == s2);
        Assert.Equal(s1.GetHashCode(), s2.GetHashCode());
    }

    [Fact]
    public void HqStateAggregator_EmitsStrictlyIncreasingRevisionValues()
    {
        var agg = new HqStateAggregator();
        var revisions = new List<long>();
        agg.StateChanged += (_, s) => revisions.Add(s.Revision);

        agg.UpdatePushedEvent(new PushedEventPayload(AgentId: "a1", State: AgentState.Working));
        agg.UpdatePushedEvent(new PushedEventPayload(AgentId: "a2", State: AgentState.Working));
        agg.SetWeatherMode(WeatherMode.Sunset);
        agg.SetDemoScenario("working");

        Assert.True(revisions.Count >= 3);
        for (int i = 1; i < revisions.Count; i++)
        {
            Assert.True(revisions[i] > revisions[i - 1], $"Revision {revisions[i]} was not strictly greater than {revisions[i - 1]}");
        }
    }

    [Fact]
    public void AppearancePolicy_OrcaBotsKeepFixedShinobi_NonOrcaGetRandomizedDistinctCharactersAndCleanNames()
    {
        var agg = new HqStateAggregator(appearanceSeed: 42);

        agg.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("orca-1", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, IsBotHome: true, Source: "orca"),
            new AgentSnapshot("orca-2", "Minato", AgentRole.Coder, "minato", AgentState.Working, IsBotHome: true, Source: "orca"),
            new AgentSnapshot("orca-3", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Idle, IsBotHome: true, Source: "orca"),
            new AgentSnapshot("orca-4", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Idle, IsBotHome: true, Source: "orca")
        }, Array.Empty<AccountInfo>());

        agg.UpdateProcessAgents(new[]
        {
            new AgentSnapshot("proc:omp:0001", "Clone H1", AgentRole.Coder, "omp", AgentState.Working, Source: "process", ProcessId: 101),
            new AgentSnapshot("proc:omp:0002", "Shadow Clone #2", AgentRole.Coder, "omp", AgentState.Idle, Source: "process", ProcessId: 102),
            new AgentSnapshot("proc:claude:0003", "Claude", AgentRole.Coder, "claude", AgentState.Working, Source: "process", ProcessId: 103)
        });

        var state = agg.GetState();
        Assert.Equal("shikamaru", state.Agents.Single(a => a.Id == "orca-1").SpriteKey);
        Assert.Equal("minato", state.Agents.Single(a => a.Id == "orca-2").SpriteKey);
        Assert.Equal("tsunade", state.Agents.Single(a => a.Id == "orca-3").SpriteKey);
        Assert.Equal("kakashi", state.Agents.Single(a => a.Id == "orca-4").SpriteKey);

        var procAgents = state.Agents.Where(a => a.Source == "process").ToList();
        Assert.Equal(3, procAgents.Count);
        Assert.All(procAgents, a => Assert.DoesNotContain("clone", a.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("OMP", procAgents.Single(a => a.Id == "proc:omp:0001").Name);
        Assert.Equal("OMP", procAgents.Single(a => a.Id == "proc:omp:0002").Name);
        Assert.Equal("Claude", procAgents.Single(a => a.Id == "proc:claude:0003").Name);
        Assert.Equal(3, procAgents.Select(a => a.SpriteKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(procAgents, a => Assert.True(AgentAppearanceRegistry.IsRandomizableSprite(a.SpriteKey)));
        Assert.All(procAgents, a => Assert.NotEqual(DeskRigKind.Default, a.DeskRig));
    }

    [Theory]
    [InlineData("variety")]
    [InlineData("mixi")]
    [InlineData("clone")]
    public void DemoScenario_VarietyMixiClone_IncludesMixiRgbRigAndZeroCloneNames(string scenario)
    {
        var agg = new HqStateAggregator(appearanceSeed: 7);
        agg.SetDemoScenario(scenario);

        var state = agg.GetState();
        Assert.Equal(scenario, state.DemoScenario);
        Assert.All(state.Agents, a => Assert.DoesNotContain("clone", a.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(state.Agents, a => a.SpriteKey == "mixi" && a.DeskRig == DeskRigKind.RgbStreamerRig);

        if (scenario is "variety" or "mixi")
        {
            var rigs = state.Agents.Where(a => a.DeskRig != DeskRigKind.Default).Select(a => a.DeskRig).Distinct().ToList();
            Assert.Equal(5, rigs.Count);
        }
    }

    [Fact]
    public void ProcessAgent_DoneCelebrationExpiry_TransitionsToIdleAndClearsWorkStartedAtAndToolName()
    {
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var agg = new HqStateAggregator(timeProvider: () => now);

        agg.UpdateProcessAgents(new[]
        {
            new AgentSnapshot("proc:omp:aaaa1111", "OMP", AgentRole.Coder, "mixi", AgentState.Working, ToolName: "thinking", Source: "process", WorkStartedAt: now)
        });
        var working = agg.GetState().Agents.Single();
        Assert.Equal(AgentState.Working, working.State);
        Assert.NotNull(working.WorkStartedAt);

        now = now.AddSeconds(5);
        agg.UpdateProcessAgents(new[]
        {
            new AgentSnapshot("proc:omp:aaaa1111", "OMP", AgentRole.Coder, "mixi", AgentState.Done, ToolName: "thinking", UpdatedAt: now, Source: "process")
        });
        var done = agg.GetState().Agents.Single();
        Assert.Equal(AgentState.Done, done.State);
        Assert.Null(done.WorkStartedAt);

        // Advance past 5-second celebration expiry
        now = now.AddSeconds(7);
        agg.Reevaluate();
        var idle = agg.GetState().Agents.Single();
        Assert.Equal(AgentState.Idle, idle.State);
        Assert.Null(idle.WorkStartedAt);
        Assert.Null(idle.ToolName);
    }
}
