using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace KageDesk.App.Native;

public record WindowSnapshot(
    IntPtr Hwnd,
    uint ProcessId,
    string ClassName,
    bool IsVisible,
    bool IsIconic,
    bool IsCloaked,
    int ExStyle,
    Rectangle Bounds
);

public class DesktopOcclusionMonitor : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly uint _ownPid;
    private bool _isWallpaperOccluded;
    private bool _isFullscreenGame;
    private bool _isSessionLocked;
    private bool _disposed;
    private bool _systemEventsSubscribed;

    public bool IsWallpaperOccluded => _isWallpaperOccluded;
    public bool IsFullscreenGame => _isFullscreenGame;
    public bool IsSessionLocked => _isSessionLocked;
    public string? TargetMonitorDeviceName { get; set; }

    public event EventHandler? OcclusionStateChanged;

    public DesktopOcclusionMonitor(int intervalMs = 500)
    {
        _ownPid = (uint)Environment.ProcessId;
        _timer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(200, intervalMs)
        };
        _timer.Tick += OnTimerTick;
    }

    public void SubscribeSessionSwitch()
    {
        if (_systemEventsSubscribed) return;
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _systemEventsSubscribed = true;
        }
        catch
        {
            // Ignore in environments where SystemEvents is unsupported
        }
    }

    public void UnsubscribeSessionSwitch()
    {
        if (_systemEventsSubscribed)
        {
            try
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                _systemEventsSubscribed = false;
            }
            catch
            {
                // Ignore in environments where SystemEvents is unsupported
            }
        }
    }

    public void Start()
    {
        _timer.Start();
        CheckOcclusion();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        CheckOcclusion();
    }

    public void CheckOcclusion()
    {
        bool occluded = false;
        bool gameDnd = false;

        try
        {
            MonitorDescriptor monitor = MonitorLayoutHelper.ResolveMonitorFromSystem(TargetMonitorDeviceName);
            Rectangle workArea = monitor.WorkingArea;
            Rectangle fullBounds = monitor.Bounds;

            // 1. Query User Notification State
            var quns = Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_ACCEPTS_NOTIFICATIONS;
            int hr = Win32Interop.SHQueryUserNotificationState(out quns);

            // 2. Collect top-level windows
            var windows = CaptureTopLevelWindows();

            // 3. Compute wallpaper occlusion against primary working area
            occluded = IsOccludedByWindows(windows, workArea, _ownPid);

            // 4. Compute game DND (notification state AND fullscreen coverage of monitor bounds)
            if (hr == 0 && IsGameOrPresentationState(quns))
            {
                gameDnd = IsFullscreenGameActive(quns, windows, fullBounds, _ownPid);
            }
        }
        catch
        {
            // Never throw from monitoring loop
        }

        bool changed = (occluded != _isWallpaperOccluded) || (gameDnd != _isFullscreenGame);
        _isWallpaperOccluded = occluded;
        _isFullscreenGame = gameDnd;

        if (changed)
        {
            OcclusionStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public static List<WindowSnapshot> CaptureTopLevelWindows()
    {
        var list = new List<WindowSnapshot>(64);
        var sbClass = new StringBuilder(256);

        Win32Interop.EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero) return true;

            Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
            bool isVisible = Win32Interop.IsWindowVisible(hwnd);
            bool isIconic = Win32Interop.IsIconic(hwnd);

            sbClass.Clear();
            Win32Interop.GetClassName(hwnd, sbClass, sbClass.Capacity);
            string className = sbClass.ToString();

            int exStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE).ToInt32();

            bool isCloaked = false;
            int hr = Win32Interop.DwmGetWindowAttribute(hwnd, Win32Interop.DWMWA_CLOAKED, out int cloakedVal, sizeof(int));
            if (hr == 0 && cloakedVal != 0)
            {
                isCloaked = true;
            }

            Rectangle bounds = Rectangle.Empty;
            if (Win32Interop.DwmGetWindowAttribute(hwnd, Win32Interop.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32Interop.RECT frameRect, Marshal.SizeOf<Win32Interop.RECT>()) == 0)
            {
                bounds = new Rectangle(frameRect.Left, frameRect.Top, frameRect.Width, frameRect.Height);
            }
            else if (Win32Interop.GetWindowRect(hwnd, out Win32Interop.RECT winRect))
            {
                bounds = new Rectangle(winRect.Left, winRect.Top, winRect.Width, winRect.Height);
            }

            list.Add(new WindowSnapshot(
                Hwnd: hwnd,
                ProcessId: pid,
                ClassName: className,
                IsVisible: isVisible,
                IsIconic: isIconic,
                IsCloaked: isCloaked,
                ExStyle: exStyle,
                Bounds: bounds
            ));

            return true;
        }, IntPtr.Zero);

        return list;
    }

    public static bool ShouldFilterWindow(WindowSnapshot window, uint ownPid)
    {
        if (window.ProcessId == ownPid && ownPid != 0) return true;
        if (!window.IsVisible || window.IsIconic || window.IsCloaked) return true;
        if ((window.ExStyle & Win32Interop.WS_EX_TOOLWINDOW) != 0) return true;
        if (window.Bounds.Width <= 10 || window.Bounds.Height <= 10) return true;

        string cls = window.ClassName;
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow")
        {
            return true;
        }

        return false;
    }

    public static bool IsWindowCoveringArea(Rectangle windowBounds, Rectangle targetArea, int marginTolerance = 4)
    {
        if (targetArea.Width <= 0 || targetArea.Height <= 0) return false;
        return windowBounds.Left <= targetArea.Left + marginTolerance
            && windowBounds.Top <= targetArea.Top + marginTolerance
            && windowBounds.Right >= targetArea.Right - marginTolerance
            && windowBounds.Bottom >= targetArea.Bottom - marginTolerance;
    }

    public static bool IsCoveringScreen(Win32Interop.RECT rect, Rectangle screen)
    {
        return IsWindowCoveringArea(new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height), screen, marginTolerance: 4);
    }

    public static bool IsOccludedByWindows(IEnumerable<WindowSnapshot> windows, Rectangle wallpaperWorkingArea, uint ownPid)
    {
        foreach (var win in windows)
        {
            if (ShouldFilterWindow(win, ownPid)) continue;
            if (IsWindowCoveringArea(win.Bounds, wallpaperWorkingArea, marginTolerance: 4))
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsFullscreenGameActive(
        Win32Interop.QUERY_USER_NOTIFICATION_STATE state,
        IEnumerable<WindowSnapshot> windows,
        Rectangle monitorBounds,
        uint ownPid)
    {
        if (!IsGameOrPresentationState(state)) return false;

        foreach (var win in windows)
        {
            if (ShouldFilterWindow(win, ownPid)) continue;
            if (IsWindowCoveringArea(win.Bounds, monitorBounds, marginTolerance: 4))
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsGameOrPresentationState(Win32Interop.QUERY_USER_NOTIFICATION_STATE state)
    {
        return state is Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D_3D_FULL_SCREEN
            or Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE
            or Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _isSessionLocked = true;
            OcclusionStateChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            _isSessionLocked = false;
            OcclusionStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void SetTestOcclusionState(bool? isWallpaperOccluded = null, bool? isFullscreenGame = null, bool? isSessionLocked = null)
    {
        if (isWallpaperOccluded.HasValue) _isWallpaperOccluded = isWallpaperOccluded.Value;
        if (isFullscreenGame.HasValue) _isFullscreenGame = isFullscreenGame.Value;
        if (isSessionLocked.HasValue) _isSessionLocked = isSessionLocked.Value;
        OcclusionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        _timer.Dispose();

        if (_systemEventsSubscribed)
        {
            try
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                _systemEventsSubscribed = false;
            }
            catch
            {
                // Ignore
            }
        }
    }
}
