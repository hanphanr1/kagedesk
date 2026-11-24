using System.Drawing;
using System.Runtime.InteropServices;

namespace KageDesk.App.Native;

public enum DesktopTopology
{
    None,
    ClassicWorkerW,
    RaisedDesktopWin11
}

public class Win32WallpaperBridge
{
    private IntPtr _attachedHwnd = IntPtr.Zero;
    private IntPtr _hostHwnd = IntPtr.Zero;
    private Form? _attachedForm;
    private IntPtr _savedStyle = IntPtr.Zero;
    private IntPtr _savedExStyle = IntPtr.Zero;
    private bool _isAttached;
    private DesktopTopology _currentTopology = DesktopTopology.None;
    private string _statusReason = "Not attached";

    public bool IsAttached => _isAttached;
    public IntPtr TargetHwnd => _hostHwnd;
    public IntPtr AttachedHwnd => _attachedHwnd;
    public DesktopTopology CurrentTopology => _currentTopology;
    public string StatusReason => _statusReason;

    public bool AttachWallpaper(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (_isAttached) return true;

        IntPtr formHandle = form.Handle;
        if (formHandle == IntPtr.Zero || !Win32Interop.IsWindow(formHandle))
        {
            _statusReason = "Invalid form window handle";
            return false;
        }

        try
        {
            // 1. Locate Progman and resolve Explorer shell PID
            IntPtr progman = Win32Interop.FindWindow("Progman", null);
            if (progman == IntPtr.Zero)
            {
                _statusReason = "Progman window not found";
                return false;
            }

            Win32Interop.GetWindowThreadProcessId(progman, out uint shellPid);
            if (shellPid == 0)
            {
                _statusReason = "Failed to determine Explorer shell PID";
                return false;
            }

            // 2. Spawn WorkerW behind desktop icons via message 0x052C
            Win32Interop.SendMessageTimeout(
                progman,
                Win32Interop.WM_SPAWN_WORKER,
                new IntPtr(0xD),
                new IntPtr(1),
                Win32Interop.SMTO_NORMAL,
                1000,
                out _);

            // 3. Detect topology: Check Raised-Desktop (Win11 24H2/25H2) first
            IntPtr defViewProgman = Win32Interop.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defViewProgman != IntPtr.Zero)
            {
                Win32Interop.GetWindowThreadProcessId(defViewProgman, out uint defViewPid);
                if (defViewPid == shellPid)
                {
                    IntPtr childWorkerW = Win32Interop.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
                    if (childWorkerW != IntPtr.Zero)
                    {
                        Win32Interop.GetWindowThreadProcessId(childWorkerW, out uint workerPid);
                        if (workerPid == shellPid)
                        {
                            return AttachRaisedDesktopTopology(form, formHandle, progman, defViewProgman);
                        }
                    }
                }
            }

            // 4. Check Classic Topology (Win10 / Win11 <= 23H2)
            IntPtr defViewParent = IntPtr.Zero;
            IntPtr defViewHwnd = IntPtr.Zero;

            Win32Interop.EnumWindows((topHwnd, _) =>
            {
                Win32Interop.GetWindowThreadProcessId(topHwnd, out uint topPid);
                if (topPid != shellPid) return true;

                IntPtr found = Win32Interop.FindWindowEx(topHwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (found != IntPtr.Zero)
                {
                    Win32Interop.GetWindowThreadProcessId(found, out uint foundPid);
                    if (foundPid == shellPid)
                    {
                        defViewParent = topHwnd;
                        defViewHwnd = found;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            if (defViewParent != IntPtr.Zero)
            {
                // Sibling WorkerW directly behind defViewParent
                IntPtr siblingWorkerW = Win32Interop.FindWindowEx(IntPtr.Zero, defViewParent, "WorkerW", null);
                if (siblingWorkerW != IntPtr.Zero)
                {
                    Win32Interop.GetWindowThreadProcessId(siblingWorkerW, out uint sibPid);
                    if (sibPid == shellPid)
                    {
                        return AttachClassicTopology(form, formHandle, siblingWorkerW);
                    }
                }
            }

            _statusReason = "No verified desktop wallpaper topology found (Progman fallback rejected)";
            return false;
        }
        catch (Exception ex)
        {
            _statusReason = $"Attach error: {ex.Message}";
            RollbackAttach(formHandle);
            return false;
        }
    }

    private bool AttachClassicTopology(Form form, IntPtr formHandle, IntPtr targetWorkerW)
    {
        // Capture original styles
        _savedStyle = Win32Interop.GetWindowLongPtr(formHandle, Win32Interop.GWL_STYLE);
        _savedExStyle = Win32Interop.GetWindowLongPtr(formHandle, Win32Interop.GWL_EXSTYLE);

        long newStyle = _savedStyle.ToInt64();
        newStyle &= ~((long)Win32Interop.WS_POPUP);
        newStyle |= Win32Interop.WS_CHILD | Win32Interop.WS_VISIBLE;

        if (!TrySetWindowLong(formHandle, Win32Interop.GWL_STYLE, new IntPtr(newStyle), out _))
        {
            _statusReason = "Failed to apply WS_CHILD style in Classic topology";
            RollbackAttach(formHandle);
            return false;
        }

        IntPtr prevParent = Win32Interop.SetParent(formHandle, targetWorkerW);
        IntPtr actualParent = Win32Interop.GetParent(formHandle);
        if (actualParent != targetWorkerW)
        {
            _statusReason = "SetParent to WorkerW failed verification";
            RollbackAttach(formHandle);
            return false;
        }

        Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var rect = new Win32Interop.RECT(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        Win32Interop.MapWindowPoints(IntPtr.Zero, targetWorkerW, ref rect, 2);

        bool posOk = Win32Interop.SetWindowPos(
            formHandle,
            Win32Interop.HWND_BOTTOM,
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height,
            Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);

        if (!posOk)
        {
            _statusReason = "SetWindowPos failed in Classic topology";
            RollbackAttach(formHandle);
            return false;
        }

        _attachedHwnd = formHandle;
        _hostHwnd = targetWorkerW;
        _attachedForm = form;
        _isAttached = true;
        _currentTopology = DesktopTopology.ClassicWorkerW;
        _statusReason = "Active (Classic WorkerW)";
        return true;
    }

    private bool AttachRaisedDesktopTopology(Form form, IntPtr formHandle, IntPtr progman, IntPtr defViewHwnd)
    {
        // Capture original styles
        _savedStyle = Win32Interop.GetWindowLongPtr(formHandle, Win32Interop.GWL_STYLE);
        _savedExStyle = Win32Interop.GetWindowLongPtr(formHandle, Win32Interop.GWL_EXSTYLE);

        long newStyle = _savedStyle.ToInt64();
        newStyle &= ~((long)Win32Interop.WS_POPUP);
        newStyle |= Win32Interop.WS_CHILD | Win32Interop.WS_VISIBLE;

        long newExStyle = _savedExStyle.ToInt64();
        newExStyle |= Win32Interop.WS_EX_LAYERED;

        if (!TrySetWindowLong(formHandle, Win32Interop.GWL_STYLE, new IntPtr(newStyle), out _) ||
            !TrySetWindowLong(formHandle, Win32Interop.GWL_EXSTYLE, new IntPtr(newExStyle), out _))
        {
            _statusReason = "Failed to configure styles for Raised-Desktop topology";
            RollbackAttach(formHandle);
            return false;
        }

        // Set layered window attributes
        Win32Interop.SetLayeredWindowAttributes(formHandle, 0, 255, Win32Interop.LWA_ALPHA);

        Win32Interop.SetParent(formHandle, progman);
        IntPtr actualParent = Win32Interop.GetParent(formHandle);
        if (actualParent != progman)
        {
            _statusReason = "SetParent to Progman failed verification in Raised-Desktop";
            RollbackAttach(formHandle);
            return false;
        }

        Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var rect = new Win32Interop.RECT(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        Win32Interop.MapWindowPoints(IntPtr.Zero, progman, ref rect, 2);

        // Place directly below SHELLDLL_DefView
        bool posOk = Win32Interop.SetWindowPos(
            formHandle,
            defViewHwnd,
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height,
            Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);

        if (!posOk)
        {
            _statusReason = "SetWindowPos below SHELLDLL_DefView failed";
            RollbackAttach(formHandle);
            return false;
        }

        _attachedHwnd = formHandle;
        _hostHwnd = progman;
        _attachedForm = form;
        _isAttached = true;
        _currentTopology = DesktopTopology.RaisedDesktopWin11;
        _statusReason = "Active (Raised-Desktop Win11 24H2+)";
        return true;
    }

    private void RollbackAttach(IntPtr formHandle)
    {
        try
        {
            if (formHandle != IntPtr.Zero && Win32Interop.IsWindow(formHandle))
            {
                Win32Interop.SetParent(formHandle, IntPtr.Zero);
                if (_savedStyle != IntPtr.Zero)
                {
                    Win32Interop.SetWindowLongPtr(formHandle, Win32Interop.GWL_STYLE, _savedStyle);
                }
                if (_savedExStyle != IntPtr.Zero)
                {
                    Win32Interop.SetWindowLongPtr(formHandle, Win32Interop.GWL_EXSTYLE, _savedExStyle);
                }
                Win32Interop.ShowWindow(formHandle, 0 /* SW_HIDE */);
            }
        }
        catch { }

        _attachedHwnd = IntPtr.Zero;
        _hostHwnd = IntPtr.Zero;
        _attachedForm = null;
        _isAttached = false;
        _currentTopology = DesktopTopology.None;
    }

    private static bool TrySetWindowLong(IntPtr hwnd, int index, IntPtr newLong, out IntPtr oldLong)
    {
        Marshal.SetLastPInvokeError(0);
        oldLong = Win32Interop.SetWindowLongPtr(hwnd, index, newLong);
        int error = Marshal.GetLastWin32Error();
        return oldLong != IntPtr.Zero || error == 0;
    }

    public void RestoreDesktopWallpaper()
    {
        if (!_isAttached || _attachedHwnd == IntPtr.Zero) return;

        IntPtr hwnd = _attachedHwnd;
        IntPtr host = _hostHwnd;

        _isAttached = false;
        _attachedHwnd = IntPtr.Zero;
        _hostHwnd = IntPtr.Zero;
        _attachedForm = null;
        _currentTopology = DesktopTopology.None;
        _statusReason = "Detached";

        try
        {
            if (hwnd != IntPtr.Zero && Win32Interop.IsWindow(hwnd))
            {
                Win32Interop.ShowWindow(hwnd, 0 /* SW_HIDE */);
                Win32Interop.SetParent(hwnd, IntPtr.Zero);

                if (_savedStyle != IntPtr.Zero)
                {
                    Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_STYLE, _savedStyle);
                }
                if (_savedExStyle != IntPtr.Zero)
                {
                    Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE, _savedExStyle);
                }
            }
        }
        catch { }

        // Invalidate host desktop window so Explorer repaints the original desktop wallpaper
        try
        {
            if (host != IntPtr.Zero && Win32Interop.IsWindow(host))
            {
                Win32Interop.InvalidateRect(host, IntPtr.Zero, true);
                Win32Interop.RedrawWindow(
                    host,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    Win32Interop.RDW_ERASE | Win32Interop.RDW_INVALIDATE | Win32Interop.RDW_ALLCHILDREN | Win32Interop.RDW_UPDATENOW);
            }
        }
        catch { }
    }
}
