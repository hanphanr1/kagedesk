using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using KageDesk.App.Native;
using KageDesk.App.UI;
using KageDesk.Core.Themes;
using KageDesk.Rendering.Canvas;
using KageDesk.Rendering.Scenes;
using Xunit;

namespace KageDesk.App.Tests;

public class LayeredDibSurfaceAndPetFormTests
{
    [Fact]
    public void LayeredDibSurface_ZeroGdiHandleDelta_AcrossRapidRedrawsAndScaleSwitches()
    {
        var proc = Process.GetCurrentProcess();

        // Warm up GDI allocations
        using (var warmup = new LayeredDibSurface(100, 100))
        {
            warmup.Resize(200, 200);
        }

        proc.Refresh();
        uint gdiStart = Win32Interop.GetGuiResources(proc.Handle, Win32Interop.GR_GDIOBJECTS);

        using (var surface = new LayeredDibSurface(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight))
        {
            using var buf = new PixelBuffer(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
            buf.FillRect(0, 0, 10, 10, ColorUtil.FromRgb(255, 0, 0));

            for (int i = 0; i < 50; i++)
            {
                // Alternate between 1x and 2x scale
                if (i % 2 == 0)
                {
                    surface.Resize(TaskbarPetRenderer.CanvasWidth, TaskbarPetRenderer.CanvasHeight);
                    surface.CopyFrom(buf);
                }
                else
                {
                    surface.Resize(TaskbarPetRenderer.CanvasWidth * 2, TaskbarPetRenderer.CanvasHeight * 2);
                    surface.BlitNearestNeighborFrom(buf, 2);
                }
            }
        }

        proc.Refresh();
        uint gdiEnd = Win32Interop.GetGuiResources(proc.Handle, Win32Interop.GR_GDIOBJECTS);
        int delta = (int)gdiEnd - (int)gdiStart;

        Assert.Equal(0, delta);
    }

    [Fact]
    public void PetOverlayForm_ScaleClamping_AndShowWithoutActivation()
    {
        var theme = ThemePackLoader.BuiltInFallback;

        // Scale 0 clamped to 1
        using var form1 = new PetOverlayForm(theme, initialScale: 0);
        Assert.Equal(1, form1.PetScale);

        // Scale 5 clamped to 2
        using var form2 = new PetOverlayForm(theme, initialScale: 5);
        Assert.Equal(2, form2.PetScale);

        // Verify ShowWithoutActivation is true
        var prop = typeof(PetOverlayForm).GetProperty("ShowWithoutActivation", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(prop);
        bool showWithoutAct = (bool)prop.GetValue(form1)!;
        Assert.True(showWithoutAct);
    }

    [Fact]
    public void AgentDetailToast_ShowWithoutActivation_IsTrue()
    {
        var prop = typeof(AgentDetailToast).GetProperty("ShowWithoutActivation", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(prop);
        // Create dummy toast without showing
        using var toast = new AgentDetailToast(new KageDesk.Core.Models.AgentSnapshot("t1", "Tester", KageDesk.Core.Models.AgentRole.Tester, "tsunade", KageDesk.Core.Models.AgentState.Idle));
        bool showWithoutAct = (bool)prop.GetValue(toast)!;
        Assert.True(showWithoutAct);
    }

    [Fact]
    public void WallpaperHostForm_ShowWithoutActivation_IsTrue()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        using var wpForm = new WallpaperHostForm(theme);
        var prop = typeof(WallpaperHostForm).GetProperty("ShowWithoutActivation", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(prop);
        bool showWithoutAct = (bool)prop.GetValue(wpForm)!;
        Assert.True(showWithoutAct);
    }
}
