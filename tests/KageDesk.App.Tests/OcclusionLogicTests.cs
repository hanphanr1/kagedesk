using System.Drawing;
using KageDesk.App.Native;
using Xunit;

namespace KageDesk.App.Tests;

public class OcclusionLogicTests
{
    [Fact]
    public void IsCoveringScreen_ExactScreenBounds_ReturnsTrue()
    {
        var screen = new Rectangle(0, 0, 1920, 1080);
        var windowRect = new Win32Interop.RECT(0, 0, 1920, 1080);

        bool covering = DesktopOcclusionMonitor.IsCoveringScreen(windowRect, screen);
        Assert.True(covering);
    }

    [Fact]
    public void IsCoveringScreen_MaximizedWindowWithNegativeBorders_ReturnsTrue()
    {
        // Windows often gives maximized windows slight negative borders (e.g. -8, -8, 1928, 1088)
        var screen = new Rectangle(0, 0, 1920, 1080);
        var windowRect = new Win32Interop.RECT(-8, -8, 1928, 1088);

        bool covering = DesktopOcclusionMonitor.IsCoveringScreen(windowRect, screen);
        Assert.True(covering);
    }

    [Fact]
    public void IsCoveringScreen_SmallWindow_ReturnsFalse()
    {
        var screen = new Rectangle(0, 0, 1920, 1080);
        var windowRect = new Win32Interop.RECT(100, 100, 900, 700);

        bool covering = DesktopOcclusionMonitor.IsCoveringScreen(windowRect, screen);
        Assert.False(covering);
    }

    [Fact]
    public void IsCoveringScreen_HalfScreenSnappedWindow_ReturnsFalse()
    {
        var screen = new Rectangle(0, 0, 1920, 1080);
        var windowRect = new Win32Interop.RECT(0, 0, 960, 1080);

        bool covering = DesktopOcclusionMonitor.IsCoveringScreen(windowRect, screen);
        Assert.False(covering);
    }

    [Fact]
    public void IsCoveringScreen_NarrowScreenCovering_ReturnsTrue()
    {
        var screen = new Rectangle(0, 0, 1366, 768);
        var windowRect = new Win32Interop.RECT(-4, -4, 1370, 772);

        bool covering = DesktopOcclusionMonitor.IsCoveringScreen(windowRect, screen);
        Assert.True(covering);
    }

    [Theory]
    [InlineData(Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D_3D_FULL_SCREEN, true)]
    [InlineData(Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE, true)]
    [InlineData(Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY, true)]
    [InlineData(Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_ACCEPTS_NOTIFICATIONS, false)]
    [InlineData(Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_NOT_PRESENT, false)]
    [InlineData(Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_QUIET_TIME, false)]
    [InlineData(Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_APP, false)]
    public void IsGameOrPresentationState_CorrectlyIdentifiesGameDnd(
        Win32Interop.QUERY_USER_NOTIFICATION_STATE state,
        bool expectedDnd)
    {
        bool actual = DesktopOcclusionMonitor.IsGameOrPresentationState(state);
        Assert.Equal(expectedDnd, actual);
    }

    [Fact]
    public void MultiMonitor_WindowOnMonitor2_DoesNotOccludeMonitor1()
    {
        var monitor1WorkArea = new Rectangle(0, 0, 1920, 1040);
        uint ownPid = 9999;

        // Window covering entire Monitor 2 (1920..3840)
        var monitor2Window = new WindowSnapshot(
            Hwnd: new IntPtr(101),
            ProcessId: 5555,
            ClassName: "Chrome_WidgetWin_1",
            IsVisible: true,
            IsIconic: false,
            IsCloaked: false,
            ExStyle: 0,
            Bounds: new Rectangle(1920, 0, 1920, 1040)
        );

        bool occluded = DesktopOcclusionMonitor.IsOccludedByWindows(new[] { monitor2Window }, monitor1WorkArea, ownPid);
        Assert.False(occluded);
    }

    [Fact]
    public void MultiMonitor_WindowOnMonitor1_DoesOccludeMonitor1()
    {
        var monitor1WorkArea = new Rectangle(0, 0, 1920, 1040);
        uint ownPid = 9999;

        // Non-focused covering window on Monitor 1 (0..1920)
        var monitor1Window = new WindowSnapshot(
            Hwnd: new IntPtr(102),
            ProcessId: 5555,
            ClassName: "Notepad",
            IsVisible: true,
            IsIconic: false,
            IsCloaked: false,
            ExStyle: 0,
            Bounds: new Rectangle(-4, -4, 1928, 1048)
        );

        bool occluded = DesktopOcclusionMonitor.IsOccludedByWindows(new[] { monitor1Window }, monitor1WorkArea, ownPid);
        Assert.True(occluded);
    }

    [Fact]
    public void WindowFiltering_FiltersOwnPid_ShellWindows_Cloaked_AndToolWindows()
    {
        uint ownPid = 1234;

        // 1. Own PID
        var ownWin = new WindowSnapshot(new IntPtr(1), ownPid, "Form", true, false, false, 0, new Rectangle(0, 0, 1920, 1080));
        Assert.True(DesktopOcclusionMonitor.ShouldFilterWindow(ownWin, ownPid));

        // 2. Shell classes
        string[] shellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow" };
        foreach (var cls in shellClasses)
        {
            var shellWin = new WindowSnapshot(new IntPtr(2), 999, cls, true, false, false, 0, new Rectangle(0, 0, 1920, 1080));
            Assert.True(DesktopOcclusionMonitor.ShouldFilterWindow(shellWin, ownPid));
        }

        // 3. Minimized (IsIconic)
        var minWin = new WindowSnapshot(new IntPtr(3), 999, "Notepad", true, true, false, 0, new Rectangle(0, 0, 1920, 1080));
        Assert.True(DesktopOcclusionMonitor.ShouldFilterWindow(minWin, ownPid));

        // 4. DWM Cloaked
        var cloakedWin = new WindowSnapshot(new IntPtr(4), 999, "ApplicationFrameWindow", true, false, true, 0, new Rectangle(0, 0, 1920, 1080));
        Assert.True(DesktopOcclusionMonitor.ShouldFilterWindow(cloakedWin, ownPid));

        // 5. Tool window (WS_EX_TOOLWINDOW = 0x80)
        var toolWin = new WindowSnapshot(new IntPtr(5), 999, "Helper", true, false, false, Win32Interop.WS_EX_TOOLWINDOW, new Rectangle(0, 0, 1920, 1080));
        Assert.True(DesktopOcclusionMonitor.ShouldFilterWindow(toolWin, ownPid));

        // 6. Tiny rect (<10px)
        var tinyWin = new WindowSnapshot(new IntPtr(6), 999, "Tiny", true, false, false, 0, new Rectangle(0, 0, 5, 5));
        Assert.True(DesktopOcclusionMonitor.ShouldFilterWindow(tinyWin, ownPid));

        // 7. Regular top-level app window (NOT filtered)
        var appWin = new WindowSnapshot(new IntPtr(7), 999, "MozillaWindowClass", true, false, false, 0, new Rectangle(0, 0, 1920, 1080));
        Assert.False(DesktopOcclusionMonitor.ShouldFilterWindow(appWin, ownPid));
    }
}
