using System.Runtime.InteropServices;
using KageDesk.App.Native;
using KageDesk.App.Services;
using KageDesk.App.UI;
using KageDesk.Core.Models;
using KageDesk.Core.Themes;

namespace KageDesk.App.Tests;

[Collection("WinFormsGdi")]
public class WallpaperBridgeAndHostTests
{
    [Fact]
    public void WallpaperHostForm_RenderFrameNow_RendersFrame0PixelsEvenWhenPaused()
    {
        var theme = ThemePackLoader.BuiltInFallback;
        using var form = new WallpaperHostForm(theme);

        // Start paused and hidden (simulating startup while occluded by a maximized window)
        form.IsPaused = true;
        Assert.False(form.Visible);
        Assert.False(form.IsAttached);

        // Before RenderFrameNow, pixel buffer has never been rendered (all zero)
        bool hasNonZeroBefore = false;
        foreach (int px in form.CurrentFramePixels)
        {
            if (px != 0)
            {
                hasNonZeroBefore = true;
                break;
            }
        }
        Assert.False(hasNonZeroBefore);

        var state = new HqState(
            Agents: new[]
            {
                new AgentSnapshot("a1", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "edit")
            },
            Accounts: Array.Empty<AccountInfo>(),
            Builds: Array.Empty<BuildInfo>(),
            Weather: EffectiveWeather.Day,
            Mood: HqMood.Busy,
            PendingVisualEvents: Array.Empty<HqEvent>(),
            Timestamp: DateTimeOffset.UtcNow);

        form.UpdateData(state, theme);
        form.RenderFrameNow();

        int nonZeroCount = 0;
        var distinctColors = new HashSet<int>();
        foreach (int px in form.CurrentFramePixels)
        {
            if (px != 0)
            {
                nonZeroCount++;
                distinctColors.Add(px);
            }
        }

        Assert.True(form.IsPaused);
        Assert.True(nonZeroCount > 1000, $"Expected rendered frame-0 pixels while paused, got {nonZeroCount} non-zero pixels.");
        Assert.True(distinctColors.Count > 5, $"Expected multi-color scene pixels, got {distinctColors.Count} distinct colors.");
    }

    [Fact]
    public void Win32WallpaperBridge_IsAttachedHostAlive_ReflectsWindowLiveness()
    {
        var bridge = new Win32WallpaperBridge();
        Assert.False(bridge.IsAttachedHostAlive());

        IntPtr hwnd;
        using (var hostForm = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false })
        {
            hwnd = hostForm.Handle;
            bridge.SeedSavedStateForTest(hwnd, Win32Interop.GetAncestor(hwnd, Win32Interop.GA_PARENT), null, null, isAttached: true);
            Assert.True(bridge.IsAttachedHostAlive());
            Assert.False(bridge.ResetAfterDestroyedHost());
        }

        // After disposing the Form, its Win32 HWND is destroyed
        Assert.False(Win32Interop.IsWindow(hwnd));
        Assert.False(bridge.IsAttachedHostAlive());
        Assert.True(bridge.ResetAfterDestroyedHost());
        Assert.False(bridge.IsAttached);
        Assert.Equal(IntPtr.Zero, bridge.AttachedHwnd);
    }

    [Fact]
    public void Win32WallpaperBridge_RollbackAttach_VerifiesGetAncestorParentAndStyleReadback()
    {
        using var ownerForm = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false };
        using var childForm = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, Owner = ownerForm };
        using var tempParent = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false };

        _ = ownerForm.Handle;
        IntPtr hwnd = childForm.Handle;
        IntPtr tempParentHwnd = tempParent.Handle;

        // Owner-sensitive GetParent returns ownerForm.Handle, while GetAncestor(GA_PARENT) returns desktop
        IntPtr originalAncestorParent = Win32Interop.GetAncestor(hwnd, Win32Interop.GA_PARENT);
        Assert.Equal(Win32Interop.GetDesktopWindow(), originalAncestorParent);

        Marshal.SetLastPInvokeError(0);
        nint savedStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE);
        Marshal.SetLastPInvokeError(0);
        nint savedExStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE);

        // Simulate partial attach mutation (reparent + mutate GWL_STYLE and GWL_EXSTYLE)
        long mutatedStyle = (savedStyle & ~((long)Win32Interop.WS_POPUP)) | Win32Interop.WS_CHILD;
        long mutatedExStyle = savedExStyle | Win32Interop.WS_EX_LAYERED;
        Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE, new IntPtr(mutatedStyle));
        Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE, new IntPtr(mutatedExStyle));
        Win32Interop.SetParent(hwnd, tempParentHwnd);

        Assert.Equal(tempParentHwnd, Win32Interop.GetAncestor(hwnd, Win32Interop.GA_PARENT));

        var bridge = new Win32WallpaperBridge();
        bridge.SeedSavedStateForTest(hwnd, originalAncestorParent, savedStyle, savedExStyle, isAttached: false);

        bool rollbackClean = bridge.RollbackAttach(childForm, hwnd);

        Assert.True(rollbackClean);
        Assert.True(bridge.LastRollbackVerified);
        Assert.Equal(originalAncestorParent, Win32Interop.GetAncestor(hwnd, Win32Interop.GA_PARENT));
        Assert.Equal(savedStyle, Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE));
        Assert.Equal(savedExStyle, Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE));
    }

    [Fact]
    public void Win32WallpaperBridge_VerifyRestoredWindowState_DetectsParentStyleAndExStyleMismatches()
    {
        using var form = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false };
        IntPtr hwnd = form.Handle;
        IntPtr actualParent = Win32Interop.GetAncestor(hwnd, Win32Interop.GA_PARENT);
        nint actualStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE);
        nint actualExStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE);

        Assert.True(Win32WallpaperBridge.VerifyRestoredWindowState(hwnd, actualParent, actualStyle, actualExStyle));
        Assert.False(Win32WallpaperBridge.VerifyRestoredWindowState(hwnd, new IntPtr(0x12345), actualStyle, actualExStyle));
        Assert.False(Win32WallpaperBridge.VerifyRestoredWindowState(hwnd, actualParent, actualStyle ^ Win32Interop.WS_CHILD, actualExStyle));
        Assert.False(Win32WallpaperBridge.VerifyRestoredWindowState(hwnd, actualParent, actualStyle, actualExStyle ^ Win32Interop.WS_EX_LAYERED));
        Assert.False(Win32WallpaperBridge.VerifyRestoredWindowState(hwnd, actualParent, null, actualExStyle));
        Assert.False(Win32WallpaperBridge.VerifyRestoredWindowState(hwnd, actualParent, actualStyle, null));
    }

    [Fact]
    public void Win32WallpaperBridge_Detach_VerifiesParentAndStyleReadbackOnSuccess()
    {
        using var childForm = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false };
        using var tempParent = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false };

        IntPtr hwnd = childForm.Handle;
        IntPtr tempParentHwnd = tempParent.Handle;
        IntPtr originalParent = Win32Interop.GetAncestor(hwnd, Win32Interop.GA_PARENT);
        nint savedStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE);
        nint savedExStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE);

        long mutatedStyle = (savedStyle & ~((long)Win32Interop.WS_POPUP)) | Win32Interop.WS_CHILD;
        long mutatedExStyle = savedExStyle | Win32Interop.WS_EX_LAYERED;
        Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE, new IntPtr(mutatedStyle));
        Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE, new IntPtr(mutatedExStyle));
        Win32Interop.SetParent(hwnd, tempParentHwnd);

        var bridge = new Win32WallpaperBridge();
        bridge.SeedSavedStateForTest(hwnd, originalParent, savedStyle, savedExStyle, isAttached: true);

        bool detached = bridge.Detach(childForm);

        Assert.True(detached);
        Assert.False(bridge.IsAttached);
        Assert.Equal(IntPtr.Zero, bridge.AttachedHwnd);
        Assert.Equal(originalParent, Win32Interop.GetAncestor(hwnd, Win32Interop.GA_PARENT));
        Assert.Equal(savedStyle, Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE));
        Assert.Equal(savedExStyle, Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE));
    }

    [Fact]
    public void Win32WallpaperBridge_Detach_ReturnsFalseWhenLiveWindowStyleOrParentReadbackFails()
    {
        using var form = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false };
        IntPtr hwnd = form.Handle;
        IntPtr originalParent = Win32Interop.GetAncestor(hwnd, Win32Interop.GA_PARENT);
        nint actualStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE);
        nint actualExStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE);

        // 1. Missing saved style while HWND is live -> Detach must return false
        var bridgeMissingStyle = new Win32WallpaperBridge();
        bridgeMissingStyle.SeedSavedStateForTest(hwnd, originalParent, savedStyle: null, savedExStyle: actualExStyle, isAttached: true);
        Assert.False(bridgeMissingStyle.Detach(form));
        Assert.True(bridgeMissingStyle.IsAttached);

        // 2. ExStyle readback mismatch while HWND is live (WS_EX_TOPMOST cannot be set via SetWindowLongPtr)
        const nint WsExTopMost = 0x00000008;
        var bridgeStyleMismatch = new Win32WallpaperBridge();
        bridgeStyleMismatch.SeedSavedStateForTest(hwnd, originalParent, actualStyle, actualExStyle | WsExTopMost, isAttached: true);
        Assert.False(bridgeStyleMismatch.Detach(form));
        Assert.True(bridgeStyleMismatch.IsAttached);

        // 3. Parent readback mismatch while HWND is live (invalid/unrestorable parent HWND)
        var bridgeParentMismatch = new Win32WallpaperBridge();
        bridgeParentMismatch.SeedSavedStateForTest(hwnd, new IntPtr(0x123456), actualStyle, actualExStyle, isAttached: true);
        Assert.False(bridgeParentMismatch.Detach(form));
        Assert.True(bridgeParentMismatch.IsAttached);
    }

    [Fact]
    public void ExplorerRestartRecovery_DeadHostAlwaysResetsAndRecreatesWithoutCallingDetach()
    {
        bool detachCalled = false;
        bool disposeCalled = false;
        bool resetCalled = false;
        bool recreateCalled = false;

        bool ok = KageDeskRuntime.ExecuteExplorerRestartRecovery(
            hadAttachment: true,
            isHostAlive: () => false,
            detachHost: () => { detachCalled = true; return true; },
            disposeOldForm: () => disposeCalled = true,
            resetAfterDestroyedHost: () => { resetCalled = true; return true; },
            markOldFormDetached: () => Assert.Fail("Should not mark dead form detached"),
            shouldAttachReplacement: true,
            createAndAttachReplacement: () => recreateCalled = true);

        Assert.True(ok);
        Assert.False(detachCalled);
        Assert.True(disposeCalled);
        Assert.True(resetCalled);
        Assert.True(recreateCalled);
    }

    [Fact]
    public void ExplorerRestartRecovery_FailedDetachWithResetFalse_LogsAndAbortsReplacement()
    {
        bool detachCalled = false;
        bool disposeCalled = false;
        bool resetCalled = false;
        bool recreateCalled = false;
        string? logged = null;

        bool ok = KageDeskRuntime.ExecuteExplorerRestartRecovery(
            hadAttachment: true,
            isHostAlive: () => true,
            detachHost: () => { detachCalled = true; return false; },
            disposeOldForm: () => disposeCalled = true,
            resetAfterDestroyedHost: () => { resetCalled = true; return false; },
            markOldFormDetached: () => Assert.Fail("Should not mark failed detach"),
            shouldAttachReplacement: true,
            createAndAttachReplacement: () => recreateCalled = true,
            logError: msg => logged = msg);

        Assert.False(ok);
        Assert.True(detachCalled);
        Assert.True(disposeCalled);
        Assert.True(resetCalled);
        Assert.False(recreateCalled);
        Assert.False(string.IsNullOrWhiteSpace(logged));
    }

    [Fact]
    public void ExplorerRestartRecovery_FailedDetachWithResetTrue_DisposesResetsAndRecreates()
    {
        bool detachCalled = false;
        bool disposeCalled = false;
        bool resetCalled = false;
        bool recreateCalled = false;

        bool ok = KageDeskRuntime.ExecuteExplorerRestartRecovery(
            hadAttachment: true,
            isHostAlive: () => true,
            detachHost: () => { detachCalled = true; return false; },
            disposeOldForm: () => disposeCalled = true,
            resetAfterDestroyedHost: () => { resetCalled = true; return true; },
            markOldFormDetached: () => Assert.Fail("Should not mark failed detach"),
            shouldAttachReplacement: true,
            createAndAttachReplacement: () => recreateCalled = true);

        Assert.True(ok);
        Assert.True(detachCalled);
        Assert.True(disposeCalled);
        Assert.True(resetCalled);
        Assert.True(recreateCalled);
    }

    [Fact]
    public void ExplorerRestartRecovery_LiveHostCleanDetach_MarksDetachedAndReattaches()
    {
        bool detachCalled = false;
        bool markedDetached = false;
        bool recreateCalled = false;

        bool ok = KageDeskRuntime.ExecuteExplorerRestartRecovery(
            hadAttachment: true,
            isHostAlive: () => true,
            detachHost: () => { detachCalled = true; return true; },
            disposeOldForm: () => Assert.Fail("Should not dispose cleanly detached form"),
            resetAfterDestroyedHost: () => { Assert.Fail("Should not reset after clean detach"); return false; },
            markOldFormDetached: () => markedDetached = true,
            shouldAttachReplacement: true,
            createAndAttachReplacement: () => recreateCalled = true);

        Assert.True(ok);
        Assert.True(detachCalled);
        Assert.True(markedDetached);
        Assert.True(recreateCalled);
    }
}
