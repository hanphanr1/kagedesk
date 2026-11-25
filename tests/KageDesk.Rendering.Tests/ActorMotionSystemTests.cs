using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using KageDesk.Core.Bridge;
using KageDesk.Core.Models;
using KageDesk.Core.State;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Sprites;
using Xunit;

namespace KageDesk.Rendering.Tests;

public class ActorMotionSystemTests
{
    private static readonly int[] CoreCentersX = { 56, 146, 238, 328 };

    [Fact]
    public void InitialState_AllStationsAtDesk_BehindDesk()
    {
        var motionSystem = new ActorMotionSystem();

        for (int i = 0; i < ActorMotionSystem.MaxStations; i++)
        {
            ref readonly var m = ref motionSystem.GetMotion(i);
            Assert.Equal(ActorMotionState.AtDesk, m.State);
            Assert.True(m.BehindDesk);
            Assert.Equal(0, m.OffsetX);
            Assert.Equal(0, m.OffsetY);
            Assert.False(m.IsInMotion);
        }
    }

    [Fact]
    public void HandoffProgression_SmoothStepOut_Walk_Deliver_And_Return()
    {
        var motionSystem = new ActorMotionSystem();
        var now = DateTimeOffset.UtcNow;

        var agents = new[]
        {
            new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle),
            new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working),
            new AgentSnapshot("demo-tester", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working)
        };

        var events = new[]
        {
            new HqEvent(1, "handoff", "Scroll handoff", now, "demo-coder", 10.0, "demo-tester")
        };

        var state = new HqState(
            Agents: agents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: events,
            Timestamp: now
        );

        int coreDeskY = 126;
        int extraDeskY = 168;

        // Step 1: Initial tick detects handoff event and enters StepOut
        motionSystem.Update(0.016f, 0.016f, state, coreDeskY, extraDeskY, 0, CoreCentersX, (i, cnt) => 100);

        Assert.True(motionSystem.IsHandoffActive);
        ref readonly var m0 = ref motionSystem.GetMotion(1); // Minato station is index 1
        Assert.Equal(ActorMotionState.StepOut, m0.State);
        Assert.True(m0.CarryingScroll);
        Assert.True(m0.IsFlashTrail);

        // Step 2: StepOut duration is ~0.35s. Advance by 0.36s -> WalkingToTarget
        motionSystem.Update(0.36f, 0.38f, state, coreDeskY, extraDeskY, 0, CoreCentersX, (i, cnt) => 100);
        ref readonly var m1 = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.WalkingToTarget, m1.State);
        Assert.False(m1.BehindDesk);

        // Step 3: Walking duration is ~0.55s. Advance by 0.60s -> Delivering
        motionSystem.Update(0.60f, 0.98f, state, coreDeskY, extraDeskY, 0, CoreCentersX, (i, cnt) => 100);
        ref readonly var m2 = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.Delivering, m2.State);
        Assert.False(m2.BehindDesk);

        // Step 4: Delivering duration is 0.45s. Advance by 0.46s -> WalkingBack (scroll dropped/delivered)
        motionSystem.Update(0.46f, 1.44f, state, coreDeskY, extraDeskY, 0, CoreCentersX, (i, cnt) => 100);
        ref readonly var m3 = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.WalkingBack, m3.State);
        Assert.False(m3.CarryingScroll);

        // Step 5: Walking back duration is ~0.60s. Advance by 0.62s -> StepBack
        motionSystem.Update(0.62f, 2.06f, state, coreDeskY, extraDeskY, 0, CoreCentersX, (i, cnt) => 100);
        ref readonly var m4 = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.StepBack, m4.State);

        // Step 6: StepBack duration is ~0.35s. Advance by 0.36s -> AtDesk complete
        motionSystem.Update(0.36f, 2.42f, state, coreDeskY, extraDeskY, 0, CoreCentersX, (i, cnt) => 100);
        ref readonly var m5 = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.AtDesk, m5.State);
        Assert.True(m5.BehindDesk);
        Assert.False(motionSystem.IsHandoffActive);
    }

    [Fact]
    public void AmbientIdleStroll_TriggersWhenCalmAndIdle_Completes3SecondCycle()
    {
        var motionSystem = new ActorMotionSystem();
        var now = DateTimeOffset.UtcNow;

        var agents = new[]
        {
            new AgentSnapshot("shikamaru", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle),
            new AgentSnapshot("minato", "Minato", AgentRole.Coder, "minato", AgentState.Idle)
        };

        var state = new HqState(
            Agents: agents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Calm,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: now
        );

        int coreDeskY = 126;
        int extraDeskY = 168;

        // Advance by 28 seconds to trigger ambient stroll (> 26s initial cooldown)
        motionSystem.Update(28.0f, 28.0f, state, coreDeskY, extraDeskY, 0, CoreCentersX, (i, cnt) => 100);

        bool anyStroll = false;
        for (int i = 0; i < 4; i++)
        {
            if (motionSystem.GetMotion(i).State == ActorMotionState.IdleStroll)
            {
                anyStroll = true;
                break;
            }
        }
        Assert.True(anyStroll);

        // Advance past full stroll duration (3.5s in multiple frame ticks) -> character should be back AtDesk
        for (int step = 0; step < 40; step++)
        {
            motionSystem.Update(0.1f, 28.1f + step * 0.1f, state, coreDeskY, extraDeskY, 0, CoreCentersX, (i, cnt) => 100);
        }
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(ActorMotionState.AtDesk, motionSystem.GetMotion(i).State);
            Assert.True(motionSystem.GetMotion(i).BehindDesk);
        }
    }

    [Fact]
    public void OutOfBoundsStationIndex_ReturnsSafeDefault()
    {
        var motionSystem = new ActorMotionSystem();
        ref readonly var safeMotion = ref motionSystem.GetMotion(999);
        Assert.Equal(ActorMotionState.AtDesk, safeMotion.State);
        Assert.True(safeMotion.BehindDesk);
    }

    [Fact]
    public void Handoff_RecipientSwapsDeskMidHandoff_UpdatesDestinationToNewDesk()
    {
        var motionSystem = new ActorMotionSystem();
        var now = DateTimeOffset.UtcNow;

        var agents = new[]
        {
            new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle),
            new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working),
            new AgentSnapshot("demo-tester", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Idle),
            new AgentSnapshot("demo-reviewer", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Idle),
            new AgentSnapshot("clone-1", "Shadow Clone 1", AgentRole.Clone, "clone", AgentState.Working),
            new AgentSnapshot("clone-2", "Shadow Clone 2", AgentRole.Clone, "clone", AgentState.Working)
        };

        var events = new[]
        {
            new HqEvent(1, "handoff", "Scroll to clone 1", now, "demo-coder", 10.0, "clone-1")
        };

        var state = new HqState(
            Agents: agents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: events,
            Timestamp: now
        );

        int coreDeskY = 126;
        int extraDeskY = 168;

        // Slot setup: station 4 has clone-1 (CenterX = 100), station 5 has clone-2 (CenterX = 250)
        var slotsInitial = new SlotBinding[]
        {
            new(0, "demo-leader", "shikamaru", AgentState.Idle, true, 56, coreDeskY),
            new(1, "demo-coder", "minato", AgentState.Working, true, 146, coreDeskY),
            new(2, "demo-tester", "tsunade", AgentState.Idle, true, 238, coreDeskY),
            new(3, "demo-reviewer", "kakashi", AgentState.Idle, true, 328, coreDeskY),
            new(4, "clone-1", "clone", AgentState.Working, true, 100, extraDeskY),
            new(5, "clone-2", "clone", AgentState.Working, true, 250, extraDeskY)
        };

        // Tick 1: start handoff -> enters StepOut toward station 4
        motionSystem.Update(0.016f, 0.016f, state, slotsInitial, coreDeskY, extraDeskY);
        ref readonly var m0 = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.StepOut, m0.State);
        Assert.Equal(4, m0.TargetSlotIndex);
        int oldTargetX = m0.TargetX;

        // Advance past StepOut (0.35s) -> WalkingToTarget
        motionSystem.Update(0.36f, 0.38f, state, slotsInitial, coreDeskY, extraDeskY);
        ref readonly var m1 = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.WalkingToTarget, m1.State);
        Assert.Equal(4, m1.TargetSlotIndex);

        // Swap station 4 and station 5: clone-1 is now at station 5 (CenterX = 250)
        var slotsSwapped = new SlotBinding[]
        {
            new(0, "demo-leader", "shikamaru", AgentState.Idle, true, 56, coreDeskY),
            new(1, "demo-coder", "minato", AgentState.Working, true, 146, coreDeskY),
            new(2, "demo-tester", "tsunade", AgentState.Idle, true, 238, coreDeskY),
            new(3, "demo-reviewer", "kakashi", AgentState.Idle, true, 328, coreDeskY),
            new(4, "clone-2", "clone", AgentState.Working, true, 100, extraDeskY),
            new(5, "clone-1", "clone", AgentState.Working, true, 250, extraDeskY)
        };

        // Tick with swapped slots: destination smoothly updates to station 5's coordinates
        motionSystem.Update(0.05f, 0.43f, state, slotsSwapped, coreDeskY, extraDeskY);
        ref readonly var m2 = ref motionSystem.GetMotion(1);
        Assert.Equal(5, m2.TargetSlotIndex);
        Assert.NotEqual(oldTargetX, m2.TargetX);
        int expectedNewTargetX = 250 + (m2.DeskX < 250 ? -16 : 16); // 234
        Assert.Equal(expectedNewTargetX, m2.TargetX);

        // Advance to Delivering: delivers at station 5 (TargetX == 234), never at old station 4
        motionSystem.Update(0.60f, 1.03f, state, slotsSwapped, coreDeskY, extraDeskY);
        ref readonly var m3 = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.Delivering, m3.State);
        Assert.Equal(expectedNewTargetX, m3.DeskX + m3.OffsetX);
    }

    [Fact]
    public void Handoff_RecipientDisappearsMidHandoff_TransitionsToWalkingBack()
    {
        var motionSystem = new ActorMotionSystem();
        var now = DateTimeOffset.UtcNow;

        var agents = new[]
        {
            new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle),
            new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working),
            new AgentSnapshot("clone-1", "Shadow Clone 1", AgentRole.Clone, "clone", AgentState.Working)
        };

        var events = new[]
        {
            new HqEvent(1, "handoff", "Scroll to clone 1", now, "demo-coder", 10.0, "clone-1")
        };

        var state = new HqState(
            Agents: agents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: events,
            Timestamp: now
        );

        int coreDeskY = 126;
        int extraDeskY = 168;

        var slotsInitial = new SlotBinding[]
        {
            new(0, "demo-leader", "shikamaru", AgentState.Idle, true, 56, coreDeskY),
            new(1, "demo-coder", "minato", AgentState.Working, true, 146, coreDeskY),
            new(2, "demo-tester", "tsunade", AgentState.Idle, false, 238, coreDeskY),
            new(3, "demo-reviewer", "kakashi", AgentState.Idle, false, 328, coreDeskY),
            new(4, "clone-1", "clone", AgentState.Working, true, 100, extraDeskY)
        };

        // Step 1: Start handoff -> StepOut
        motionSystem.Update(0.016f, 0.016f, state, slotsInitial, coreDeskY, extraDeskY);
        Assert.Equal(ActorMotionState.StepOut, motionSystem.GetMotion(1).State);

        // Advance to WalkingToTarget
        motionSystem.Update(0.36f, 0.38f, state, slotsInitial, coreDeskY, extraDeskY);
        Assert.Equal(ActorMotionState.WalkingToTarget, motionSystem.GetMotion(1).State);

        // Recipient disappears: slot 4 is now unoccupied
        var slotsDisappeared = new SlotBinding[]
        {
            new(0, "demo-leader", "shikamaru", AgentState.Idle, true, 56, coreDeskY),
            new(1, "demo-coder", "minato", AgentState.Working, true, 146, coreDeskY),
            new(2, "demo-tester", "tsunade", AgentState.Idle, false, 238, coreDeskY),
            new(3, "demo-reviewer", "kakashi", AgentState.Idle, false, 328, coreDeskY),
            new(4, null, "clone", AgentState.Idle, false, 100, extraDeskY)
        };

        // Next tick: clean transition to WalkingBack with CarryingScroll = false
        motionSystem.Update(0.05f, 0.43f, state, slotsDisappeared, coreDeskY, extraDeskY);
        ref readonly var mBack = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.WalkingBack, mBack.State);
        Assert.False(mBack.CarryingScroll);

        // Advance past walkback and stepback: returns cleanly to AtDesk
        for (int i = 0; i < 20; i++)
        {
            motionSystem.Update(0.1f, 0.53f + i * 0.1f, state, slotsDisappeared, coreDeskY, extraDeskY);
        }
        ref readonly var mFinal = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.AtDesk, mFinal.State);
        Assert.True(mFinal.BehindDesk);
    }

    [Fact]
    public void Handoff_AliasBoundRecipientReplacedInSameSlot_TransitionsToWalkingBack_NeverDelivers()
    {
        var motionSystem = new ActorMotionSystem();
        var now = DateTimeOffset.UtcNow;

        var initialAgents = new[]
        {
            new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle),
            new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working),
            new AgentSnapshot("original", "Original Tester", AgentRole.Tester, "tsunade", AgentState.Working),
            new AgentSnapshot("demo-reviewer", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Idle)
        };

        var events = new[]
        {
            new HqEvent(1, "handoff", "Scroll to QA", now, "demo-coder", 10.0, "qa")
        };

        var initialState = new HqState(
            Agents: initialAgents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: events,
            Timestamp: now
        );

        int coreDeskY = 126;
        int extraDeskY = 168;

        var slotsFrame0 = new SlotBinding[]
        {
            new(0, "demo-leader", "shikamaru", AgentState.Idle, true, 56, coreDeskY),
            new(1, "demo-coder", "minato", AgentState.Working, true, 146, coreDeskY),
            new(2, "original", "tsunade", AgentState.Working, true, 238, coreDeskY),
            new(3, "demo-reviewer", "kakashi", AgentState.Idle, true, 328, coreDeskY)
        };

        // Frame 0: "qa" resolves to concrete agent "original" in tester slot 2
        motionSystem.Update(0.016f, 0.016f, initialState, slotsFrame0, coreDeskY, extraDeskY);
        ref readonly var m0 = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.StepOut, m0.State);
        Assert.Equal("original", m0.TargetAgentId);
        Assert.Equal(2, m0.TargetSlotIndex);
        Assert.False(m0.TargetBoundBySpriteKey);

        // Advance past StepOut into WalkingToTarget while "original" is still present
        motionSystem.Update(0.36f, 0.38f, initialState, slotsFrame0, coreDeskY, extraDeskY);
        Assert.Equal(ActorMotionState.WalkingToTarget, motionSystem.GetMotion(1).State);

        // Subsequent frames: "original" disappears and "replacement" takes the exact same tester slot
        var replacedAgents = new[]
        {
            new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle),
            new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working),
            new AgentSnapshot("replacement", "Replacement Tester", AgentRole.Tester, "tsunade", AgentState.Working),
            new AgentSnapshot("demo-reviewer", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Idle)
        };
        var replacedState = initialState with { Agents = replacedAgents };

        var slotsReplaced = new SlotBinding[]
        {
            new(0, "demo-leader", "shikamaru", AgentState.Idle, true, 56, coreDeskY),
            new(1, "demo-coder", "minato", AgentState.Working, true, 146, coreDeskY),
            new(2, "replacement", "tsunade", AgentState.Working, true, 238, coreDeskY),
            new(3, "demo-reviewer", "kakashi", AgentState.Idle, true, 328, coreDeskY)
        };

        bool everEnteredDelivering = false;
        bool sawWalkingBack = false;

        // Advance through arrival and return ticks
        for (int step = 0; step < 40; step++)
        {
            float t = 0.43f + step * 0.05f;
            motionSystem.Update(0.05f, t, replacedState, slotsReplaced, coreDeskY, extraDeskY);
            ref readonly var current = ref motionSystem.GetMotion(1);

            if (step == 0)
            {
                Assert.Equal(ActorMotionState.WalkingBack, current.State);
                Assert.False(current.CarryingScroll);
            }

            if (current.State == ActorMotionState.WalkingBack)
            {
                sawWalkingBack = true;
            }

            if (current.State == ActorMotionState.Delivering)
            {
                everEnteredDelivering = true;
            }
        }

        Assert.True(sawWalkingBack, "Sender should transition to WalkingBack when original recipient is replaced.");
        Assert.False(everEnteredDelivering, "Sender must NEVER enter Delivering for a replacement agent.");
        ref readonly var mFinal = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.AtDesk, mFinal.State);
        Assert.True(mFinal.BehindDesk);
        Assert.False(motionSystem.IsHandoffActive);
    }

    [Fact]
    public void Handoff_AliasBoundSameConcreteRecipientMovesSlot_ContinuesTrackingConcreteAgent()
    {
        var motionSystem = new ActorMotionSystem();
        var now = DateTimeOffset.UtcNow;

        var initialAgents = new[]
        {
            new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle),
            new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working),
            new AgentSnapshot("original", "Original Tester", AgentRole.Tester, "tsunade", AgentState.Working),
            new AgentSnapshot("demo-reviewer", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Idle)
        };

        var events = new[]
        {
            new HqEvent(1, "handoff", "Scroll to QA", now, "demo-coder", 10.0, "qa")
        };

        var state = new HqState(
            Agents: initialAgents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: events,
            Timestamp: now
        );

        int coreDeskY = 126;
        int extraDeskY = 168;

        var slotsInitial = new SlotBinding[]
        {
            new(0, "demo-leader", "shikamaru", AgentState.Idle, true, 56, coreDeskY),
            new(1, "demo-coder", "minato", AgentState.Working, true, 146, coreDeskY),
            new(2, "original", "tsunade", AgentState.Working, true, 238, coreDeskY),
            new(3, "demo-reviewer", "kakashi", AgentState.Idle, true, 328, coreDeskY),
            new(4, null, "clone", AgentState.Idle, false, 192, extraDeskY)
        };

        motionSystem.Update(0.016f, 0.016f, state, slotsInitial, coreDeskY, extraDeskY);
        motionSystem.Update(0.36f, 0.38f, state, slotsInitial, coreDeskY, extraDeskY);
        Assert.Equal(ActorMotionState.WalkingToTarget, motionSystem.GetMotion(1).State);

        // "original" moves to slot 4 while "replacement" takes slot 2
        var slotsMoved = new SlotBinding[]
        {
            new(0, "demo-leader", "shikamaru", AgentState.Idle, true, 56, coreDeskY),
            new(1, "demo-coder", "minato", AgentState.Working, true, 146, coreDeskY),
            new(2, "replacement", "tsunade", AgentState.Working, true, 238, coreDeskY),
            new(3, "demo-reviewer", "kakashi", AgentState.Idle, true, 328, coreDeskY),
            new(4, "original", "tsunade", AgentState.Working, true, 192, extraDeskY)
        };

        motionSystem.Update(0.05f, 0.43f, state, slotsMoved, coreDeskY, extraDeskY);
        ref readonly var mTracked = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.WalkingToTarget, mTracked.State);
        Assert.Equal(4, mTracked.TargetSlotIndex);
        int expectedTargetX = 192 + (mTracked.DeskX < 192 ? -16 : 16);
        Assert.Equal(expectedTargetX, mTracked.TargetX);
        Assert.Equal(extraDeskY + 22, mTracked.TargetY);

        // Advance to Delivering -> delivers to "original" at slot 4
        motionSystem.Update(0.60f, 1.03f, state, slotsMoved, coreDeskY, extraDeskY);
        ref readonly var mDelivered = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.Delivering, mDelivered.State);
        Assert.Equal(expectedTargetX, mDelivered.DeskX + mDelivered.OffsetX);
        Assert.Equal(extraDeskY + 22, mDelivered.DeskY + mDelivered.OffsetY);
    }

    [Fact]
    public void Handoff_ExactIdQaTakesPrecedenceOverTesterRoleAlias()
    {
        var motionSystem = new ActorMotionSystem();
        var now = DateTimeOffset.UtcNow;

        var agents = new[]
        {
            new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle),
            new AgentSnapshot("demo-coder", "Minato", AgentRole.Coder, "minato", AgentState.Working),
            new AgentSnapshot("original", "Original Tester", AgentRole.Tester, "tsunade", AgentState.Working),
            new AgentSnapshot("qa", "Literal QA Clone", AgentRole.Clone, "clone", AgentState.Working)
        };

        var events = new[]
        {
            new HqEvent(1, "handoff", "Scroll to literal qa agent", now, "demo-coder", 10.0, "qa")
        };

        var state = new HqState(
            Agents: agents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: events,
            Timestamp: now
        );

        int coreDeskY = 126;
        int extraDeskY = 168;

        var slots = new SlotBinding[]
        {
            new(0, "demo-leader", "shikamaru", AgentState.Idle, true, 56, coreDeskY),
            new(1, "demo-coder", "minato", AgentState.Working, true, 146, coreDeskY),
            new(2, "original", "tsunade", AgentState.Working, true, 238, coreDeskY),
            new(3, null, "kakashi", AgentState.Idle, false, 328, coreDeskY),
            new(4, "qa", "clone", AgentState.Working, true, 192, extraDeskY)
        };

        motionSystem.Update(0.016f, 0.016f, state, slots, coreDeskY, extraDeskY);
        ref readonly var m0 = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.StepOut, m0.State);
        Assert.Equal("qa", m0.TargetAgentId);
        Assert.Equal(4, m0.TargetSlotIndex);
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task BridgeToRenderer_ConcreteLiteralQaRecipientDepartsBeforeConsumption_HandoffSkipped_NeverDeliversToActiveTester()
    {
        int port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        const string token = "test-token-bridge-renderer-qa-departed";

        // Snapshot 1: sender, literal agent ID "qa", and active tester "tester-1"
        aggregator.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("sender", "Minato", AgentRole.Coder, "minato", AgentState.Working),
            new AgentSnapshot("tester-1", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working),
            new AgentSnapshot("qa", "Literal QA Worker", AgentRole.Clone, "clone", AgentState.Working)
        }, Array.Empty<AccountInfo>());

        await using var bridge = new LocalEventBridge(aggregator, port, requiredToken: token);
        bridge.Start();

        using var client = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/event")
        {
            Content = new StringContent(
                """{"type":"handoff","fromAgentId":"sender","toAgentId":"qa","message":"Scroll for literal qa"}""",
                Encoding.UTF8,
                "application/json")
        };
        req.Headers.Add("X-KageDesk-Token", token);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var queuedHandoff = aggregator.GetState().PendingVisualEvents
            .Single(e => string.Equals(e.Type, "handoff", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("sender", queuedHandoff.AgentId);
        Assert.Equal("qa", queuedHandoff.TargetAgentId);
        Assert.True(queuedHandoff.FromIsConcreteId);
        Assert.True(queuedHandoff.ToIsConcreteId);

        // Snapshot 2: literal "qa" departs BEFORE ActorMotionSystem consumes the queued event
        aggregator.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("sender", "Minato", AgentRole.Coder, "minato", AgentState.Working),
            new AgentSnapshot("tester-1", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working)
        }, Array.Empty<AccountInfo>());

        var stateAfterDeparture = aggregator.GetState();
        Assert.Contains(stateAfterDeparture.PendingVisualEvents, e => e.SequenceId == queuedHandoff.SequenceId);

        const int coreDeskY = 126;
        const int extraDeskY = 168;
        var slotsAfterDeparture = new SlotBinding[]
        {
            new(0, null, "shikamaru", AgentState.Idle, false, 56, coreDeskY),
            new(1, "sender", "minato", AgentState.Working, true, 146, coreDeskY),
            new(2, "tester-1", "tsunade", AgentState.Working, true, 238, coreDeskY),
            new(3, null, "kakashi", AgentState.Idle, false, 328, coreDeskY)
        };

        var motionSystem = new ActorMotionSystem();
        for (int step = 0; step < 40; step++)
        {
            motionSystem.Update(0.05f, 0.05f * (step + 1), stateAfterDeparture, slotsAfterDeparture, coreDeskY, extraDeskY);
            Assert.False(motionSystem.IsHandoffActive);
            var senderMotion = motionSystem.GetMotion(1);
            Assert.Equal(ActorMotionState.AtDesk, senderMotion.State);
            Assert.NotEqual(ActorMotionState.Delivering, senderMotion.State);
            Assert.False(senderMotion.CarryingScroll);
        }
    }

    [Fact]
    public async Task BridgeToRenderer_ConcreteLiteralCoderSenderDepartsBeforeConsumption_HandoffSkipped_NotHijackedByActiveCoder()
    {
        int port = GetAvailablePort();
        var aggregator = new HqStateAggregator();
        const string token = "test-token-bridge-renderer-coder-departed";

        // Snapshot 1: literal agent ID "coder", active coder "coder-1", and recipient "tester-1"
        aggregator.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("coder-1", "Minato", AgentRole.Coder, "minato", AgentState.Working),
            new AgentSnapshot("tester-1", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working),
            new AgentSnapshot("coder", "Literal Coder Clone", AgentRole.Clone, "clone", AgentState.Working)
        }, Array.Empty<AccountInfo>());

        await using var bridge = new LocalEventBridge(aggregator, port, requiredToken: token);
        bridge.Start();

        using var client = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/event")
        {
            Content = new StringContent(
                """{"type":"handoff","fromAgentId":"coder","toAgentId":"tester-1","message":"Scroll from literal coder"}""",
                Encoding.UTF8,
                "application/json")
        };
        req.Headers.Add("X-KageDesk-Token", token);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var queuedHandoff = aggregator.GetState().PendingVisualEvents
            .Single(e => string.Equals(e.Type, "handoff", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("coder", queuedHandoff.AgentId);
        Assert.Equal("tester-1", queuedHandoff.TargetAgentId);
        Assert.True(queuedHandoff.FromIsConcreteId);
        Assert.True(queuedHandoff.ToIsConcreteId);

        // Snapshot 2: literal "coder" departs BEFORE ActorMotionSystem consumes the queued event
        aggregator.UpdateOrcaSnapshot(new[]
        {
            new AgentSnapshot("coder-1", "Minato", AgentRole.Coder, "minato", AgentState.Working),
            new AgentSnapshot("tester-1", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working)
        }, Array.Empty<AccountInfo>());

        var stateAfterDeparture = aggregator.GetState();
        Assert.Contains(stateAfterDeparture.PendingVisualEvents, e => e.SequenceId == queuedHandoff.SequenceId);

        const int coreDeskY = 126;
        const int extraDeskY = 168;
        var slotsAfterDeparture = new SlotBinding[]
        {
            new(0, null, "shikamaru", AgentState.Idle, false, 56, coreDeskY),
            new(1, "coder-1", "minato", AgentState.Working, true, 146, coreDeskY),
            new(2, "tester-1", "tsunade", AgentState.Working, true, 238, coreDeskY),
            new(3, null, "kakashi", AgentState.Idle, false, 328, coreDeskY)
        };

        var motionSystem = new ActorMotionSystem();
        for (int step = 0; step < 40; step++)
        {
            motionSystem.Update(0.05f, 0.05f * (step + 1), stateAfterDeparture, slotsAfterDeparture, coreDeskY, extraDeskY);
            Assert.False(motionSystem.IsHandoffActive);
            var coderMotion = motionSystem.GetMotion(1);
            Assert.Equal(ActorMotionState.AtDesk, coderMotion.State);
            Assert.NotEqual(ActorMotionState.Delivering, coderMotion.State);
            Assert.False(coderMotion.CarryingScroll);
        }
    }

    [Fact]
    public void Handoff_UnresolvedAliasFlagsFalse_ResolvesAndAnimatesNormally()
    {
        var motionSystem = new ActorMotionSystem();
        var now = DateTimeOffset.UtcNow;

        var agents = new[]
        {
            new AgentSnapshot("demo-leader", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Idle),
            new AgentSnapshot("coder-1", "Minato", AgentRole.Coder, "minato", AgentState.Working),
            new AgentSnapshot("tester-1", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working)
        };

        // Default FromIsConcreteId = false, ToIsConcreteId = false (unresolved aliases)
        var events = new[]
        {
            new HqEvent(1, "handoff", "Alias handoff", now, "coder", 10.0, "qa")
        };

        var state = new HqState(
            Agents: agents,
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: events,
            Timestamp: now
        );

        const int coreDeskY = 126;
        const int extraDeskY = 168;
        var slots = new SlotBinding[]
        {
            new(0, "demo-leader", "shikamaru", AgentState.Idle, true, 56, coreDeskY),
            new(1, "coder-1", "minato", AgentState.Working, true, 146, coreDeskY),
            new(2, "tester-1", "tsunade", AgentState.Working, true, 238, coreDeskY),
            new(3, null, "kakashi", AgentState.Idle, false, 328, coreDeskY)
        };

        motionSystem.Update(0.016f, 0.016f, state, slots, coreDeskY, extraDeskY);
        Assert.True(motionSystem.IsHandoffActive);
        ref readonly var m0 = ref motionSystem.GetMotion(1);
        Assert.Equal(ActorMotionState.StepOut, m0.State);
        Assert.Equal("coder-1", m0.BoundAgentId);
        Assert.Equal("tester-1", m0.TargetAgentId);

        motionSystem.Update(0.36f, 0.38f, state, slots, coreDeskY, extraDeskY);
        Assert.Equal(ActorMotionState.WalkingToTarget, motionSystem.GetMotion(1).State);

        motionSystem.Update(0.60f, 0.98f, state, slots, coreDeskY, extraDeskY);
        Assert.Equal(ActorMotionState.Delivering, motionSystem.GetMotion(1).State);

        motionSystem.Update(0.46f, 1.44f, state, slots, coreDeskY, extraDeskY);
        Assert.Equal(ActorMotionState.WalkingBack, motionSystem.GetMotion(1).State);

        motionSystem.Update(0.62f, 2.06f, state, slots, coreDeskY, extraDeskY);
        Assert.Equal(ActorMotionState.StepBack, motionSystem.GetMotion(1).State);

        motionSystem.Update(0.36f, 2.42f, state, slots, coreDeskY, extraDeskY);
        Assert.Equal(ActorMotionState.AtDesk, motionSystem.GetMotion(1).State);
        Assert.False(motionSystem.IsHandoffActive);
    }
}



