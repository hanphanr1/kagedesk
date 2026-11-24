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

    [Fact]
    public void PetOverlayForm_SetScale_SuccessAndPreservation()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        using var form = new PetOverlayForm(theme, initialScale: 1);
        Assert.Equal(1, form.PetScale);

        bool success = form.SetScale(2);
        Assert.True(success);
        Assert.Equal(2, form.PetScale);

        // Same scale is no-op and returns true
        bool sameSuccess = form.SetScale(2);
        Assert.True(sameSuccess);
        Assert.Equal(2, form.PetScale);
    }

    [Fact]
    public void PetOverlayForm_ApplyRenderState_AtomicallyAppliesPauseAndState()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        using var form = new PetOverlayForm(theme, initialScale: 1);
        form.Start();

        var activeState = new KageDesk.Core.Models.HqState(
            Agents: new[]
            {
                new KageDesk.Core.Models.AgentSnapshot("a1", "Worker", KageDesk.Core.Models.AgentRole.Coder, "minato", KageDesk.Core.Models.AgentState.Working)
            },
            Accounts: Array.Empty<KageDesk.Core.Models.AccountInfo>(),
            Builds: Array.Empty<KageDesk.Core.Models.BuildInfo>(),
            Weather: KageDesk.Core.Models.EffectiveWeather.Day,
            Mood: KageDesk.Core.Models.HqMood.Busy,
            PendingVisualEvents: Array.Empty<KageDesk.Core.Models.HqEvent>()
        );

        // 1. Paused -> Form must NOT be visible even with active agent
        form.ApplyRenderState(activeState, theme, petPaused: true, petHiddenByDndOrLock: false);
        Assert.False(form.Visible);

        // 2. Unpaused -> Active agent causes form to be visible
        form.ApplyRenderState(activeState, theme, petPaused: false, petHiddenByDndOrLock: false);
        Assert.True(form.Visible);

        // 3. Hidden by DND/Lock -> Form hides immediately
        form.ApplyRenderState(activeState, theme, petPaused: false, petHiddenByDndOrLock: true);
        Assert.False(form.Visible);
    }

    [Fact]
    public void UiDispatcherForm_PropertiesAndHandleCreation()
    {
        using var dispatcher = new UiDispatcherForm();
        Assert.Equal(System.Windows.Forms.FormBorderStyle.None, dispatcher.FormBorderStyle);
        Assert.False(dispatcher.ShowInTaskbar);
        Assert.NotEqual(IntPtr.Zero, dispatcher.Handle);
    }
}

