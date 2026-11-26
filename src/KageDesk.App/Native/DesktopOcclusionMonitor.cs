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

    private IReadOnlyList<MonitorDescriptor>? _cachedMonitors;
    private long _cachedMonitorsTick;

    public bool IsWallpaperOccluded => _isWallpaperOccluded;
    public bool IsFullscreenGame => _isFullscreenGame;
    public bool IsSessionLocked => _isSessionLocked;

    /// <summary>
    /// Monitor device name hosting the live wallpaper (evaluated for wallpaper occlusion).
    /// </summary>
    public string? WallpaperMonitorDeviceName { get; set; }

    /// <summary>
    /// Backward-compatible alias for <see cref="WallpaperMonitorDeviceName"/>.
    /// </summary>
    public string? TargetMonitorDeviceName
    {
        get => WallpaperMonitorDeviceName;
        set => WallpaperMonitorDeviceName = value;
    }

    /// <summary>
    /// Monitor device name hosting the Taskbar Pet (evaluated together with all displays for Fullscreen Game DND).
    /// </summary>
    public string? PetMonitorDeviceName { get; set; }

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

    public void InvalidateMonitorCache()
    {
        _cachedMonitors = null;
        _cachedMonitorsTick = 0;
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

    private IReadOnlyList<MonitorDescriptor> GetMonitorsCached()
    {
        long nowTick = Environment.TickCount64;
        if (_cachedMonitors != null && (nowTick - _cachedMonitorsTick) < 2000)
        {
            return _cachedMonitors;
        }

        _cachedMonitors = MonitorLayoutHelper.GetConnectedMonitors();
        _cachedMonitorsTick = nowTick;
        return _cachedMonitors;
    }

    public void CheckOcclusion()
    {
        bool occluded = false;
        bool gameDnd = false;

        try
        {
            var monitors = GetMonitorsCached();

            // 1. Query User Notification State
            var quns = Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_ACCEPTS_NOTIFICATIONS;
            int hr = Win32Interop.SHQueryUserNotificationState(out quns);
            Win32Interop.QUERY_USER_NOTIFICATION_STATE effectiveQuns = hr == 0
                ? quns
                : Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_ACCEPTS_NOTIFICATIONS;

            // 2. Collect candidate visible top-level windows (pre-filtering cheap flags & bounds before DWM IPC)
            var windows = CaptureTopLevelWindows(_ownPid, monitors);

            // 3. Evaluate split dual-monitor occlusion & fullscreen game DND
            (occluded, gameDnd) = EvaluateDualMonitorOcclusionAndDnd(
                monitors,
                WallpaperMonitorDeviceName,
                PetMonitorDeviceName,
                effectiveQuns,
                windows,
                _ownPid);
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

    /// <summary>
    /// Evaluates wallpaper occlusion strictly against the wallpaper display's WorkingArea,
    /// while evaluating Fullscreen Game DND against the pet display and all connected displays
    /// so placing the wallpaper on a secondary monitor never breaks primary gaming DND.
    /// </summary>
    public static (bool IsWallpaperOccluded, bool IsFullscreenGameDnd) EvaluateDualMonitorOcclusionAndDnd(
        IReadOnlyList<MonitorDescriptor> monitors,
        string? wallpaperMonitorDeviceName,
        string? petMonitorDeviceName,
        Win32Interop.QUERY_USER_NOTIFICATION_STATE quns,
        IEnumerable<WindowSnapshot> windows,
        uint ownPid)
    {
        MonitorDescriptor wallpaperMonitor = MonitorLayoutHelper.ResolveMonitor(monitors, wallpaperMonitorDeviceName);
        MonitorDescriptor petMonitor = MonitorLayoutHelper.ResolveMonitor(monitors, petMonitorDeviceName);

        bool occluded = IsOccludedByWindows(windows, wallpaperMonitor.WorkingArea, ownPid);

        bool gameDnd = false;
        if (quns is Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D_3D_FULL_SCREEN
            or Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
        {
            gameDnd = true;
        }
        else if (IsGameOrPresentationState(quns))
        {
            // Check pet monitor first
            if (IsFullscreenGameActive(quns, windows, petMonitor.Bounds, ownPid))
            {
                gameDnd = true;
            }
            else if (monitors != null)
            {
                // Also check all connected displays so a fullscreen game on primary (or any monitor) triggers DND
                for (int i = 0; i < monitors.Count; i++)
                {
                    if (string.Equals(monitors[i].DeviceName, petMonitor.DeviceName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (IsFullscreenGameActive(quns, windows, monitors[i].Bounds, ownPid))
                    {
                        gameDnd = true;
                        break;
                    }
                }
            }
        }

        return (occluded, gameDnd);
    }

    public static List<WindowSnapshot> CaptureTopLevelWindows(
        uint ownPidToSkip = 0,
        IReadOnlyList<MonitorDescriptor>? candidateMonitors = null)
    {
        var list = new List<WindowSnapshot>(16);
        var sbClass = new StringBuilder(128);

        Win32Interop.EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero) return true;

            // Fast Win32 filters BEFORE any cross-process DWM IPC!
            if (!Win32Interop.IsWindowVisible(hwnd)) return true;
            if (Win32Interop.IsIconic(hwnd)) return true;

            Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
            if (ownPidToSkip != 0 && pid == ownPidToSkip) return true;

            int exStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE).ToInt32();
            if ((exStyle & Win32Interop.WS_EX_TOOLWINDOW) != 0) return true;

            // Fast rough bounds check via GetWindowRect before DWM IPC
            if (!Win32Interop.GetWindowRect(hwnd, out Win32Interop.RECT winRect) ||
                winRect.Width <= 10 || winRect.Height <= 10)
            {
                return true;
            }

            // Since GetWindowRect is always >= DWMWA_EXTENDED_FRAME_BOUNDS, any small window
            // that cannot cover any connected monitor's working area can skip DWM IPC completely!
            if (candidateMonitors != null && candidateMonitors.Count > 0)
            {
                var roughBounds = new Rectangle(winRect.Left, winRect.Top, winRect.Width, winRect.Height);
                bool couldCoverAnyMonitor = false;
                for (int i = 0; i < candidateMonitors.Count; i++)
                {
                    if (IsWindowCoveringArea(roughBounds, candidateMonitors[i].WorkingArea, marginTolerance: 16))
                    {
                        couldCoverAnyMonitor = true;
                        break;
                    }
                }
                if (!couldCoverAnyMonitor)
                {
                    return true;
                }
            }

            sbClass.Clear();
            Win32Interop.GetClassName(hwnd, sbClass, sbClass.Capacity);
            string className = sbClass.ToString();
            if (IsDesktopShellClass(className)) return true;

            // Only remaining visible, non-iconic, non-tool, non-shell candidate windows incur DWM IPC
            bool isCloaked = false;
            int hr = Win32Interop.DwmGetWindowAttribute(hwnd, Win32Interop.DWMWA_CLOAKED, out int cloakedVal, sizeof(int));
            if (hr == 0 && cloakedVal != 0)
            {
                isCloaked = true;
            }

            Rectangle bounds;
            if (Win32Interop.DwmGetWindowAttribute(hwnd, Win32Interop.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32Interop.RECT frameRect, Marshal.SizeOf<Win32Interop.RECT>()) == 0)
            {
                bounds = new Rectangle(frameRect.Left, frameRect.Top, frameRect.Width, frameRect.Height);
            }
            else
            {
                bounds = new Rectangle(winRect.Left, winRect.Top, winRect.Width, winRect.Height);
            }

            list.Add(new WindowSnapshot(
                Hwnd: hwnd,
                ProcessId: pid,
                ClassName: className,
                IsVisible: true,
                IsIconic: false,
                IsCloaked: isCloaked,
                ExStyle: exStyle,
                Bounds: bounds
            ));

            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static bool IsDesktopShellClass(string cls)
    {
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow";
    }

    public static bool ShouldFilterWindow(WindowSnapshot window, uint ownPid)
    {
        if (window.ProcessId == ownPid && ownPid != 0) return true;
        if (!window.IsVisible || window.IsIconic || window.IsCloaked) return true;
        if ((window.ExStyle & Win32Interop.WS_EX_TOOLWINDOW) != 0) return true;
        if (window.Bounds.Width <= 10 || window.Bounds.Height <= 10) return true;
        if (IsDesktopShellClass(window.ClassName)) return true;

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
