using KageDesk.Core.Models;
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
}
