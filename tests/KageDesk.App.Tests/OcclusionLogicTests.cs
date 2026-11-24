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
}
