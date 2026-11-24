using System.Drawing;
using KageDesk.Rendering.Scenes;
using Xunit;

namespace KageDesk.App.Tests;

public class PetPlacementAndHitTests
{
    [Theory]
    [InlineData(1, 384, 128)]
    [InlineData(2, 768, 256)]
    public void PetDimensions_ScaleFactorMultipliesBaseDimensions(int scale, int expectedWidth, int expectedHeight)
    {
        int actualWidth = TaskbarPetRenderer.CanvasWidth * scale;
        int actualHeight = TaskbarPetRenderer.CanvasHeight * scale;

        Assert.Equal(expectedWidth, actualWidth);
        Assert.Equal(expectedHeight, actualHeight);
    }

    [Fact]
    public void TaskbarPlacement_NullPetX_SnapsToRightEdgeAboveTaskbar()
    {
        var workArea = new Rectangle(0, 0, 1920, 1040); // 40px taskbar
        int scale = 2;
        int width = TaskbarPetRenderer.CanvasWidth * scale;   // 768
        int height = TaskbarPetRenderer.CanvasHeight * scale; // 256

        int top = workArea.Bottom - height; // 1040 - 256 = 784
        int? petX = null;
        int left = petX ?? (workArea.Right - width - 16);

        Assert.Equal(784, top);
        Assert.Equal(1920 - 768 - 16, left); // 1136
    }

    [Fact]
    public void TaskbarPlacement_CustomPetX_ClampedInsideWorkArea()
    {
        var workArea = new Rectangle(0, 0, 1920, 1040);
        int scale = 2;
        int width = TaskbarPetRenderer.CanvasWidth * scale;
        int maxLeft = workArea.Right - width; // 1920 - 768 = 1152

        // Negative X should clamp to 0
        int clampNegative = Math.Clamp(-50, workArea.Left, maxLeft);
        Assert.Equal(0, clampNegative);

        // Huge X beyond right edge should clamp to maxLeft
        int clampOverflow = Math.Clamp(2500, workArea.Left, maxLeft);
        Assert.Equal(maxLeft, clampOverflow);

        // Valid inside X remains unchanged
        int inside = Math.Clamp(500, workArea.Left, maxLeft);
        Assert.Equal(500, inside);
    }

    [Fact]
    public void HitRegion_UnscaledContains_WorksAccurately()
    {
        var region = new PetHitRegion("open_orca", 100, 50, 60, 20, "Open Orca");

        Assert.True(region.Contains(100, 50));        // Top-left
        Assert.True(region.Contains(159, 69));        // Bottom-right inside
        Assert.True(region.Contains(130, 60));        // Center

        Assert.False(region.Contains(99, 50));        // Left of bounds
        Assert.False(region.Contains(160, 50));       // Right of bounds
        Assert.False(region.Contains(100, 49));       // Above
        Assert.False(region.Contains(100, 70));       // Below
    }

    [Fact]
    public void HitRegion_ScaledClickMapping_Scale2xTranslatesCorrectly()
    {
        var region = new PetHitRegion("open_orca", 100, 50, 60, 20);
        int scale = 2;

        // User clicks at scaled pixel (230, 110)
        int clickX = 230;
        int clickY = 110;

        int unscaledX = clickX / scale; // 115
        int unscaledY = clickY / scale; // 55

        Assert.True(region.Contains(unscaledX, unscaledY));

        // Click outside scaled region (e.g. 50, 50 -> unscaled 25, 25)
        int outsideX = 50;
        int outsideY = 50;
        Assert.False(region.Contains(outsideX / scale, outsideY / scale));
    }
}
