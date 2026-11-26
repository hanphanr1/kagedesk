using System.Drawing;
using System.Runtime.InteropServices;
using KageDesk.App.UI;

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
    private IntPtr _originalParent = IntPtr.Zero;
    private nint? _savedStyle;
    private nint? _savedExStyle;
    private bool _isAttached;
    private bool _lastRollbackVerified;
    private DesktopTopology _currentTopology = DesktopTopology.None;
    private string _statusReason = "Not attached";
    private string? _targetMonitorDeviceName;

    public bool IsAttached => _isAttached;
    public bool LastRollbackVerified => _lastRollbackVerified;
    public IntPtr TargetHwnd => _hostHwnd;
    public IntPtr AttachedHwnd => _attachedHwnd;
    public DesktopTopology CurrentTopology => _currentTopology;
    public string StatusReason => _statusReason;
    public string? TargetMonitorDeviceName => _targetMonitorDeviceName;

    public bool IsAttachedHostAlive()
    {
        return _attachedHwnd != IntPtr.Zero && Win32Interop.IsWindow(_attachedHwnd);
    }

    public bool AttachWallpaper(Form form, string? targetMonitorDeviceName = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        _targetMonitorDeviceName = targetMonitorDeviceName;
        if (_isAttached) return true;

        IntPtr formHandle = form.Handle;
        if (formHandle == IntPtr.Zero || !Win32Interop.IsWindow(formHandle))
        {
            _statusReason = "Invalid form window handle";
            return false;
        }

        // Capture original parent (via owner-insensitive GetAncestor) and styles with exact nint? preservation
        _originalParent = Win32Interop.GetAncestor(formHandle, Win32Interop.GA_PARENT);

        Marshal.SetLastPInvokeError(0);
        nint style = Win32Interop.GetWindowLongPtr(formHandle, Win32Interop.GWL_STYLE);
        int errStyle = Marshal.GetLastWin32Error();
        _savedStyle = (style != 0 || errStyle == 0) ? style : null;

        Marshal.SetLastPInvokeError(0);
        nint exStyle = Win32Interop.GetWindowLongPtr(formHandle, Win32Interop.GWL_EXSTYLE);
        int errExStyle = Marshal.GetLastWin32Error();
        _savedExStyle = (exStyle != 0 || errExStyle == 0) ? exStyle : null;

        try
        {
            // 1. Obtain verified Explorer shell window via GetShellWindow()
            IntPtr shellWindow = Win32Interop.GetShellWindow();
            if (shellWindow == IntPtr.Zero || !Win32Interop.IsWindow(shellWindow))
            {
                IntPtr progmanCandidate = Win32Interop.FindWindow("Progman", null);
                if (progmanCandidate != IntPtr.Zero && progmanCandidate == Win32Interop.GetShellWindow())
                {
                    shellWindow = progmanCandidate;
                }
                else
                {
                    _statusReason = "Verified Explorer shell window not found";
                    return false;
                }
            }

            Win32Interop.GetWindowThreadProcessId(shellWindow, out uint shellPid);
            if (shellPid == 0)
            {
                _statusReason = "Failed to determine Explorer shell PID";
                return false;
            }

            IntPtr progman = Win32Interop.FindWindow("Progman", null);
            if (progman == IntPtr.Zero)
            {
                progman = shellWindow;
            }

            Win32Interop.GetWindowThreadProcessId(progman, out uint progmanPid);
            if (progmanPid != shellPid)
            {
                _statusReason = "Progman PID does not match verified shell PID";
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
            RollbackAttach(form, formHandle);
            return false;
        }
    }

    private bool AttachClassicTopology(Form form, IntPtr formHandle, IntPtr targetWorkerW)
    {
        long baseStyle = _savedStyle.HasValue ? _savedStyle.Value : Win32Interop.GetWindowLongPtr(formHandle, Win32Interop.GWL_STYLE).ToInt64();
        long newStyle = baseStyle;
        newStyle &= ~((long)Win32Interop.WS_POPUP);
        newStyle |= Win32Interop.WS_CHILD | Win32Interop.WS_VISIBLE;

        if (!TrySetWindowLong(formHandle, Win32Interop.GWL_STYLE, new IntPtr(newStyle), out _))
        {
            _statusReason = "Failed to apply WS_CHILD style in Classic topology";
            RollbackAttach(form, formHandle);
            return false;
        }

        Win32Interop.SetParent(formHandle, targetWorkerW);
        IntPtr actualParent = Win32Interop.GetAncestor(formHandle, Win32Interop.GA_PARENT);
        if (actualParent != targetWorkerW)
        {
            _statusReason = "SetParent to WorkerW failed verification";
            RollbackAttach(form, formHandle);
            return false;
        }

        Rectangle targetRect = ComputeHostClientRect(targetWorkerW, _targetMonitorDeviceName);

        bool posOk = Win32Interop.SetWindowPos(
            formHandle,
            Win32Interop.HWND_BOTTOM,
            targetRect.Left,
            targetRect.Top,
            targetRect.Width,
            targetRect.Height,
            Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);

        if (!posOk)
        {
            _statusReason = "SetWindowPos failed in Classic topology";
            RollbackAttach(form, formHandle);
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
        long baseStyle = _savedStyle.HasValue ? _savedStyle.Value : Win32Interop.GetWindowLongPtr(formHandle, Win32Interop.GWL_STYLE).ToInt64();
        long newStyle = baseStyle;
        newStyle &= ~((long)Win32Interop.WS_POPUP);
        newStyle |= Win32Interop.WS_CHILD | Win32Interop.WS_VISIBLE;

        long baseExStyle = _savedExStyle.HasValue ? _savedExStyle.Value : Win32Interop.GetWindowLongPtr(formHandle, Win32Interop.GWL_EXSTYLE).ToInt64();
        long newExStyle = baseExStyle | Win32Interop.WS_EX_LAYERED;

        if (!TrySetWindowLong(formHandle, Win32Interop.GWL_STYLE, new IntPtr(newStyle), out _) ||
            !TrySetWindowLong(formHandle, Win32Interop.GWL_EXSTYLE, new IntPtr(newExStyle), out _))
        {
            _statusReason = "Failed to configure styles for Raised-Desktop topology";
            RollbackAttach(form, formHandle);
            return false;
        }

        // Set layered window attributes - if false, immediately roll back and return failure
        if (!Win32Interop.SetLayeredWindowAttributes(formHandle, 0, 255, Win32Interop.LWA_ALPHA))
        {
            _statusReason = "SetLayeredWindowAttributes failed in Raised-Desktop";
            RollbackAttach(form, formHandle);
            return false;
        }

        Win32Interop.SetParent(formHandle, progman);
        IntPtr actualParent = Win32Interop.GetAncestor(formHandle, Win32Interop.GA_PARENT);
        if (actualParent != progman)
        {
            _statusReason = "SetParent to Progman failed verification in Raised-Desktop";
            RollbackAttach(form, formHandle);
            return false;
        }

        Rectangle targetRect = ComputeHostClientRect(progman, _targetMonitorDeviceName);

        // Place directly below SHELLDLL_DefView
        bool posOk = Win32Interop.SetWindowPos(
            formHandle,
            defViewHwnd,
            targetRect.Left,
            targetRect.Top,
            targetRect.Width,
            targetRect.Height,
            Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);

        if (!posOk)
        {
            _statusReason = "SetWindowPos below SHELLDLL_DefView failed";
            RollbackAttach(form, formHandle);
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

    public bool RepositionForMonitor(string? targetMonitorDeviceName)
    {
        _targetMonitorDeviceName = targetMonitorDeviceName;
        if (!_isAttached || _attachedHwnd == IntPtr.Zero || _hostHwnd == IntPtr.Zero ||
            !Win32Interop.IsWindow(_attachedHwnd) || !Win32Interop.IsWindow(_hostHwnd))
        {
            return false;
        }

        Rectangle targetRect = ComputeHostClientRect(_hostHwnd, _targetMonitorDeviceName);
        return Win32Interop.SetWindowPos(
            _attachedHwnd,
            IntPtr.Zero,
            targetRect.Left,
            targetRect.Top,
            targetRect.Width,
            targetRect.Height,
            Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);
    }

    private static Rectangle ComputeHostClientRect(IntPtr hostHwnd, string? targetMonitorDeviceName)
    {
        var monitor = MonitorLayoutHelper.ResolveMonitorFromSystem(targetMonitorDeviceName);
        Rectangle virtualScreen = SystemInformation.VirtualScreen;
        Rectangle virtualOffset = MonitorLayoutHelper.ComputeWallpaperVirtualOffset(monitor.Bounds, virtualScreen);

        if (hostHwnd != IntPtr.Zero && Win32Interop.IsWindow(hostHwnd))
        {
            var rect = new Win32Interop.RECT(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Right, monitor.Bounds.Bottom);
            Win32Interop.MapWindowPoints(IntPtr.Zero, hostHwnd, ref rect, 2);
            if (rect.Width > 0 && rect.Height > 0)
            {
                return new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height);
            }
        }

        return virtualOffset;
    }

    internal static IntPtr ResolveSetParentTarget(IntPtr originalParent)
    {
        IntPtr desktop = Win32Interop.GetDesktopWindow();
        return (originalParent == IntPtr.Zero || originalParent == desktop) ? IntPtr.Zero : originalParent;
    }

    internal static IntPtr ResolveExpectedParent(IntPtr originalParent)
    {
        return originalParent == IntPtr.Zero ? Win32Interop.GetDesktopWindow() : originalParent;
    }

    internal static bool VerifyRestoredWindowState(
        IntPtr formHandle,
        IntPtr expectedParent,
        nint? expectedStyle,
        nint? expectedExStyle)
    {
        if (formHandle == IntPtr.Zero || !Win32Interop.IsWindow(formHandle))
        {
            return false;
        }

        if (!expectedStyle.HasValue || !expectedExStyle.HasValue)
        {
            return false;
        }

        IntPtr actualParent = Win32Interop.GetAncestor(formHandle, Win32Interop.GA_PARENT);
        if (actualParent != expectedParent)
        {
            return false;
        }

        Marshal.SetLastPInvokeError(0);
        nint actualStyle = Win32Interop.GetWindowLongPtr(formHandle, Win32Interop.GWL_STYLE);
        int errStyle = Marshal.GetLastWin32Error();
        if ((actualStyle == 0 && errStyle != 0) || actualStyle != expectedStyle.Value)
        {
            return false;
        }

        Marshal.SetLastPInvokeError(0);
        nint actualExStyle = Win32Interop.GetWindowLongPtr(formHandle, Win32Interop.GWL_EXSTYLE);
        int errExStyle = Marshal.GetLastWin32Error();
        if ((actualExStyle == 0 && errExStyle != 0) || actualExStyle != expectedExStyle.Value)
        {
            return false;
        }

        return true;
    }

    private bool RestoreAndVerifyWindowState(IntPtr formHandle)
    {
        bool restoreOk = true;
        Win32Interop.ShowWindow(formHandle, 0 /* SW_HIDE */);

        IntPtr setParentTarget = ResolveSetParentTarget(_originalParent);
        IntPtr expectedParent = ResolveExpectedParent(_originalParent);
        Win32Interop.SetParent(formHandle, setParentTarget);

        if (_savedStyle.HasValue)
        {
            if (!TrySetWindowLong(formHandle, Win32Interop.GWL_STYLE, new IntPtr(_savedStyle.Value), out _))
            {
                restoreOk = false;
            }
        }
        else
        {
            restoreOk = false;
        }

        if (_savedExStyle.HasValue)
        {
            if (!TrySetWindowLong(formHandle, Win32Interop.GWL_EXSTYLE, new IntPtr(_savedExStyle.Value), out _))
            {
                restoreOk = false;
            }
        }
        else
        {
            restoreOk = false;
        }

        // Verify restored parent via GetAncestor(GA_PARENT) AND read back both GWL_STYLE & GWL_EXSTYLE
        if (restoreOk && !VerifyRestoredWindowState(formHandle, expectedParent, _savedStyle, _savedExStyle))
        {
            restoreOk = false;
        }

        return restoreOk;
    }

    internal bool RollbackAttach(Form? attemptedForm, IntPtr formHandle)
    {
        bool restoreOk = false;
        try
        {
            if (attemptedForm is WallpaperHostForm hostForm)
            {
                hostForm.StopRendering();
            }

            if (formHandle != IntPtr.Zero && Win32Interop.IsWindow(formHandle))
            {
                restoreOk = RestoreAndVerifyWindowState(formHandle);

                if (!restoreOk)
                {
                    _statusReason = "Attach rollback verification failed: unable to restore styles or parent";
                }
            }
        }
        catch (Exception ex)
        {
            restoreOk = false;
            _statusReason = $"Attach rollback error: {ex.Message}";
        }
        finally
        {
            _lastRollbackVerified = restoreOk;
            _attachedHwnd = IntPtr.Zero;
            _hostHwnd = IntPtr.Zero;
            _attachedForm = null;
            _isAttached = false;
            _currentTopology = DesktopTopology.None;
            _savedStyle = null;
            _savedExStyle = null;
            _originalParent = IntPtr.Zero;
        }

        return restoreOk;
    }

    internal void SeedSavedStateForTest(IntPtr attachedHwnd, IntPtr originalParent, nint? savedStyle, nint? savedExStyle, bool isAttached = false)
    {
        _attachedHwnd = attachedHwnd;
        _originalParent = originalParent;
        _savedStyle = savedStyle;
        _savedExStyle = savedExStyle;
        _isAttached = isAttached;
    }

    public bool ResetAfterDestroyedHost()
    {
        if (_attachedHwnd != IntPtr.Zero && Win32Interop.IsWindow(_attachedHwnd))
        {
            return false;
        }

        _isAttached = false;
        _attachedHwnd = IntPtr.Zero;
        _hostHwnd = IntPtr.Zero;
        _attachedForm = null;
        _currentTopology = DesktopTopology.None;
        _savedStyle = null;
        _savedExStyle = null;
        _originalParent = IntPtr.Zero;
        _statusReason = "Reset after destroyed host";
        return true;
    }

    private static bool TrySetWindowLong(IntPtr hwnd, int index, IntPtr newLong, out IntPtr oldLong)
    {
        Marshal.SetLastPInvokeError(0);
        oldLong = Win32Interop.SetWindowLongPtr(hwnd, index, newLong);
        int error = Marshal.GetLastWin32Error();
        return oldLong != IntPtr.Zero || error == 0;
    }

    public bool Detach(Form? form = null)
    {
        if (!_isAttached && _attachedHwnd == IntPtr.Zero)
        {
            _statusReason = "Detached";
            return true;
        }

        IntPtr hwnd = _attachedHwnd;
        IntPtr host = _hostHwnd;
        Form? targetForm = form ?? _attachedForm;

        if (targetForm is WallpaperHostForm hostForm)
        {
            hostForm.StopRendering();
        }

        bool detached = true;
        try
        {
            if (hwnd != IntPtr.Zero && Win32Interop.IsWindow(hwnd))
            {
                bool restored = RestoreAndVerifyWindowState(hwnd);
                if (!restored && Win32Interop.IsWindow(hwnd))
                {
                    detached = false;
                }
            }
        }
        catch
        {
            detached = false;
        }

        if (!detached)
        {
            _statusReason = "Detach failed: unable to restore parent window or styles";
            return false;
        }

        _isAttached = false;
        _attachedHwnd = IntPtr.Zero;
        _hostHwnd = IntPtr.Zero;
        _attachedForm = null;
        _currentTopology = DesktopTopology.None;
        _savedStyle = null;
        _savedExStyle = null;
        _originalParent = IntPtr.Zero;
        _statusReason = "Detached";

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

        return true;
    }

    public bool RestoreDesktopWallpaper() => Detach(_attachedForm);
}
