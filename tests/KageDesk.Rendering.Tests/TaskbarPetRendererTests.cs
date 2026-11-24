using KageDesk.Core.Models;
using KageDesk.Core.Themes;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Scenes;

namespace KageDesk.Rendering.Tests;

public class TaskbarPetRendererTests
{
    private static HqState MakeState(params AgentSnapshot[] agents) => new(
        Agents: agents,
        Accounts: Array.Empty<AccountInfo>(),
        Builds: Array.Empty<BuildInfo>(),
        Weather: EffectiveWeather.Day,
        Mood: HqMood.Calm,
        PendingVisualEvents: Array.Empty<HqEvent>(),
        Timestamp: DateTimeOffset.UtcNow);

    [Fact]
    public void Render_NeedsApproval_HasTransparentBackgroundCharacterAndExactHitRegions()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        var state = MakeState(
            new AgentSnapshot("shika-1", "Shikamaru (Leader)", AgentRole.Leader, "shikamaru", AgentState.NeedsApproval,
                QuestionPrompt: "Approve production deployment to main cluster?"));

        IReadOnlyList<PetHitRegion> regions = renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.0f);

        double transparentRatio = ComputeTransparentRatio(buf);
        Assert.True(transparentRatio > 0.55, $"Expected >55% transparent pixels, got {transparentRatio:P1}.");

        // Top-left corner must be 100% transparent
        Assert.Equal(0, buf.GetPixel(0, 0));
        Assert.Equal(0, buf.GetPixel(20, 10));

        // Character pixels present near bottom edge on right
        Assert.True(CountNonZeroPixelsInRegion(buf, 326, 92, 36, 34) > 120);

        // Hit regions must include open_orca, dismiss, and agent:shika-1
        PetHitRegion openOrca = Assert.Single(regions, r => r.ActionId == "open_orca");
        PetHitRegion dismiss = Assert.Single(regions, r => r.ActionId == "dismiss");
        PetHitRegion agent = Assert.Single(regions, r => r.ActionId == "agent:shika-1");

        AssertValidRegionInBounds(openOrca);
        AssertValidRegionInBounds(dismiss);
        AssertValidRegionInBounds(agent);
        Assert.False(openOrca.Contains(dismiss.X + dismiss.Width / 2, dismiss.Y + dismiss.Height / 2));

        // Verify button regions actually have rendered non-transparent pixels
        Assert.True(CountNonZeroPixelsInRegion(buf, openOrca.X, openOrca.Y, openOrca.Width, openOrca.Height) > 400);
        Assert.True(CountNonZeroPixelsInRegion(buf, dismiss.X, dismiss.Y, dismiss.Width, dismiss.Height) > 100);
    }

    [Fact]
    public void Render_WorkingMultipleAgents_RendersUpToThreeMiniDesksAndAgentHitRegions()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        var state = MakeState(
            new AgentSnapshot("a1", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "edit"),
            new AgentSnapshot("a2", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, ToolName: "test"),
            new AgentSnapshot("a3", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Working, ToolName: "review"),
            new AgentSnapshot("a4", "Clone #1", AgentRole.Clone, "clone", AgentState.Working, ToolName: "exec"));

        IReadOnlyList<PetHitRegion> regions = renderer.Render(buf, state, ThemePackLoader.BuiltInFallback, 1.5f);

        Assert.True(ComputeTransparentRatio(buf) > 0.70);
        Assert.Equal(3, regions.Count(r => r.ActionId.StartsWith("agent:", StringComparison.Ordinal)));
        Assert.Contains(regions, r => r.ActionId == "open_orca");
    }

    [Fact]
    public void Render_FailedAndDoneModes_RespectPriorityAndTransparency()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);

        // Failed has priority over Done & Working
        var failState = MakeState(
            new AgentSnapshot("w1", "Minato", AgentRole.Coder, "minato", AgentState.Working),
            new AgentSnapshot("d1", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Done),
            new AgentSnapshot("f1", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Failed, LastMessage: "Build error"));

        IReadOnlyList<PetHitRegion> failRegions = renderer.Render(buf, failState, ThemePackLoader.BuiltInFallback, 1.0f);
        Assert.True(ComputeTransparentRatio(buf) > 0.60);
        Assert.Contains(failRegions, r => r.ActionId == "agent:f1");
        Assert.Contains(failRegions, r => r.ActionId == "open_orca");
        Assert.Contains(failRegions, r => r.ActionId == "dismiss");

        // Done mode when no approval/failed
        var doneState = MakeState(
            new AgentSnapshot("d1", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Done, TaskTitle: "All tests green"));
        IReadOnlyList<PetHitRegion> doneRegions = renderer.Render(buf, doneState, ThemePackLoader.BuiltInFallback, 1.0f);
        Assert.True(ComputeTransparentRatio(buf) > 0.70);
        Assert.Contains(doneRegions, r => r.ActionId == "agent:d1");
    }

    [Fact]
    public void Render_IdleMode_HonorsShowIdleChillToggle()
    {
        var renderer = new TaskbarPetRenderer();
        using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
        var idleState = MakeState();

        // showIdleChill = false -> 100% transparent
        IReadOnlyList<PetHitRegion> hiddenRegions = renderer.Render(buf, idleState, ThemePackLoader.BuiltInFallback, 0.5f, showIdleChill: false);
        Assert.Empty(hiddenRegions);
        Assert.Equal(1.0, ComputeTransparentRatio(buf));

        // showIdleChill = true -> renders chill character with >85% transparent background
        IReadOnlyList<PetHitRegion> chillRegions = renderer.Render(buf, idleState, ThemePackLoader.BuiltInFallback, 0.5f, showIdleChill: true);
        Assert.Single(chillRegions);
        double ratio = ComputeTransparentRatio(buf);
        Assert.True(ratio > 0.85 && ratio < 0.99, $"Expected idle chill transparent ratio between 85% and 99%, got {ratio:P1}.");
    }

    private static double ComputeTransparentRatio(PixelBuffer buf)
    {
        int transparent = 0;
        ReadOnlySpan<int> pixels = buf.ReadOnlyPixels;
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] == 0) transparent++;
        }
        return (double)transparent / pixels.Length;
    }

    private static int CountNonZeroPixelsInRegion(PixelBuffer buf, int x, int y, int w, int h)
    {
        int count = 0;
        for (int py = y; py < y + h; py++)
        {
            for (int px = x; px < x + w; px++)
            {
                if (buf.GetPixel(px, py) != 0) count++;
            }
        }
        return count;
    }

    private static void AssertValidRegionInBounds(PetHitRegion r)
    {
        Assert.True(r.Width > 0 && r.Height > 0);
        Assert.True(r.X >= 0 && r.Y >= 0);
        Assert.True(r.X + r.Width <= TaskbarPetRenderer.CanvasWidth);
        Assert.True(r.Y + r.Height <= TaskbarPetRenderer.CanvasHeight);
    }
}
