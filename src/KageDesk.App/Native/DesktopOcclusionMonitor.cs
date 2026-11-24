using System.Drawing;
using Microsoft.Win32;

namespace KageDesk.App.Native;

public class DesktopOcclusionMonitor : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private bool _isWallpaperOccluded;
    private bool _isFullscreenGame;
    private bool _isSessionLocked;
    private bool _disposed;

    public bool IsWallpaperOccluded => _isWallpaperOccluded;
    public bool IsFullscreenGame => _isFullscreenGame;
    public bool IsSessionLocked => _isSessionLocked;

    public event EventHandler? OcclusionStateChanged;

    public DesktopOcclusionMonitor(int intervalMs = 500)
    {
        _timer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(200, intervalMs)
        };
        _timer.Tick += OnTimerTick;

        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        catch
        {
            // Ignore in environments where SystemEvents is unsupported
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
            // 1. Check fullscreen / DND state
            int hr = Win32Interop.SHQueryUserNotificationState(out var state);
            if (hr == 0)
            {
                gameDnd = IsGameOrPresentationState(state);
            }

            // 2. Check foreground window covering desktop
            IntPtr fgHwnd = Win32Interop.GetForegroundWindow();
            if (fgHwnd != IntPtr.Zero)
            {
                Rectangle screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                if (Win32Interop.IsZoomed(fgHwnd))
                {
                    occluded = true;
                }
                else if (Win32Interop.GetWindowRect(fgHwnd, out var rect))
                {
                    occluded = IsCoveringScreen(rect, screen);
                }
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

    public static bool IsGameOrPresentationState(Win32Interop.QUERY_USER_NOTIFICATION_STATE state)
    {
        return state is Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D_3D_FULL_SCREEN
            or Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE
            or Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY;
    }

    public static bool IsCoveringScreen(Win32Interop.RECT rect, Rectangle screen)
    {
        if (screen.Width <= 0 || screen.Height <= 0) return false;
        // Margin allowance of 4px for borders/shadows
        return rect.Left <= screen.Left + 4
            && rect.Top <= screen.Top + 4
            && rect.Right >= screen.Right - 4
            && rect.Bottom >= screen.Bottom - 4;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        _timer.Dispose();

        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        catch
        {
            // Ignore
        }
    }
}
