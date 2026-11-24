using System.Drawing;
using System.Text;

namespace KageDesk.App.Native;

public class Win32WallpaperBridge
{
    private IntPtr _targetHwnd = IntPtr.Zero;
    private Form? _attachedForm;
    private string? _originalWallpaperPath;
    private bool _isAttached;

    public bool IsAttached => _isAttached;
    public IntPtr TargetHwnd => _targetHwnd;

    public bool AttachWallpaper(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (_isAttached) return true;

        try
        {
            // Save original wallpaper path for clean restore on exit
            _originalWallpaperPath = GetCurrentWallpaperPath();

            // Step 1: Find Progman
            IntPtr progman = Win32Interop.FindWindow("Progman", null);
            if (progman == IntPtr.Zero)
            {
                return false;
            }

            // Step 2: Send message 0x052C to Progman to spawn WorkerW
            Win32Interop.SendMessageTimeout(
                progman,
                Win32Interop.WM_SPAWN_WORKER,
                new IntPtr(0xD),
                new IntPtr(1),
                Win32Interop.SMTO_NORMAL,
                1000,
                out _);

            IntPtr targetParent = FindTargetDesktopHwnd(progman);
            if (targetParent == IntPtr.Zero)
            {
                // Fallback attempt: retry 0x052C with (0, 0)
                Win32Interop.SendMessageTimeout(
                    progman,
                    Win32Interop.WM_SPAWN_WORKER,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    Win32Interop.SMTO_NORMAL,
                    1000,
                    out _);
                targetParent = FindTargetDesktopHwnd(progman);
            }

            if (targetParent == IntPtr.Zero)
            {
                return false;
            }

            _targetHwnd = targetParent;
            _attachedForm = form;

            // Step 4: Configure form as WS_CHILD, attach to target WorkerW/Progman
            IntPtr style = Win32Interop.GetWindowLongPtr(form.Handle, Win32Interop.GWL_STYLE);
            long newStyle = style.ToInt64();
            newStyle &= ~((long)Win32Interop.WS_POPUP);
            newStyle |= Win32Interop.WS_CHILD | Win32Interop.WS_VISIBLE;
            Win32Interop.SetWindowLongPtr(form.Handle, Win32Interop.GWL_STYLE, new IntPtr(newStyle));

            Win32Interop.SetParent(form.Handle, targetParent);

            // Compute primary screen bounds in targetParent coordinates
            Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            var rect = new Win32Interop.RECT(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            Win32Interop.MapWindowPoints(IntPtr.Zero, targetParent, ref rect, 2);

            Win32Interop.SetWindowPos(
                form.Handle,
                Win32Interop.HWND_BOTTOM,
                rect.Left,
                rect.Top,
                rect.Width,
                rect.Height,
                Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);

            _isAttached = true;
            return true;
        }
        catch
        {
            // Fallback gracefully without throwing
            return false;
        }
    }

    public void RestoreDesktopWallpaper()
    {
        if (_attachedForm != null && !_attachedForm.IsDisposed)
        {
            try
            {
                Win32Interop.SetParent(_attachedForm.Handle, IntPtr.Zero);
                _attachedForm.Hide();
            }
            catch
            {
                // Ignore any teardown errors
            }
        }

        _attachedForm = null;
        _isAttached = false;
        _targetHwnd = IntPtr.Zero;

        // Step 5: Restore desktop wallpaper via SPI_SETDESKWALLPAPER
        try
        {
            string? path = _originalWallpaperPath;
            if (string.IsNullOrEmpty(path))
            {
                path = GetCurrentWallpaperPath();
            }

            if (!string.IsNullOrEmpty(path))
            {
                Win32Interop.SystemParametersInfo(
                    Win32Interop.SPI_SETDESKWALLPAPER,
                    0,
                    path,
                    Win32Interop.SPIF_UPDATEINIFILE | Win32Interop.SPIF_SENDCHANGE);
            }
        }
        catch
        {
            // Ignore wallpaper refresh errors
        }
    }

    private static IntPtr FindTargetDesktopHwnd(IntPtr progman)
    {
        // Step 3a: Check if SHELLDLL_DefView is directly under Progman (Win11 24H2/25H2 raised desktop)
        IntPtr defView = Win32Interop.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
        {
            IntPtr childWorkerW = Win32Interop.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            if (childWorkerW != IntPtr.Zero)
            {
                return childWorkerW;
            }
            return progman;
        }

        // Step 3b: Classic Win10 / Win11 <=23H2: iterate top-level windows to find SHELLDLL_DefView
        IntPtr shellDllParent = IntPtr.Zero;
        Win32Interop.EnumWindows((topHwnd, _) =>
        {
            IntPtr found = Win32Interop.FindWindowEx(topHwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (found != IntPtr.Zero)
            {
                shellDllParent = topHwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (shellDllParent != IntPtr.Zero)
        {
            // Find the WorkerW window immediately following or spawned alongside
            IntPtr nextWorkerW = Win32Interop.FindWindowEx(IntPtr.Zero, shellDllParent, "WorkerW", null);
            if (nextWorkerW != IntPtr.Zero)
            {
                return nextWorkerW;
            }
        }

        return IntPtr.Zero;
    }

    private static string? GetCurrentWallpaperPath()
    {
        try
        {
            var sb = new StringBuilder(260);
            if (Win32Interop.SystemParametersInfo(Win32Interop.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
            {
                return sb.ToString();
            }
        }
        catch
        {
            // Fallback
        }
        return null;
    }
}
