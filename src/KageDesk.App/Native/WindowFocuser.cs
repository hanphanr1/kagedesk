using System.Diagnostics;

namespace KageDesk.App.Native;

public static class WindowFocuser
{
    public static bool TryFocusOrca()
    {
        try
        {
            IntPtr targetHwnd = FindOrcaWindow();
            if (targetHwnd == IntPtr.Zero)
            {
                return false;
            }

            if (Win32Interop.IsIconic(targetHwnd))
            {
                Win32Interop.ShowWindow(targetHwnd, Win32Interop.SW_RESTORE);
            }
            else
            {
                Win32Interop.ShowWindow(targetHwnd, Win32Interop.SW_SHOW);
            }

            IntPtr fgHwnd = Win32Interop.GetForegroundWindow();
            uint fgThread = Win32Interop.GetWindowThreadProcessId(fgHwnd, out _);
            uint curThread = Win32Interop.GetCurrentThreadId();

            if (fgThread != curThread && fgThread != 0)
            {
                bool attached = false;
                try
                {
                    attached = Win32Interop.AttachThreadInput(curThread, fgThread, true);
                    Win32Interop.BringWindowToTop(targetHwnd);
                    return Win32Interop.SetForegroundWindow(targetHwnd);
                }
                finally
                {
                    if (attached)
                    {
                        Win32Interop.AttachThreadInput(curThread, fgThread, false);
                    }
                }
            }
            else
            {
                Win32Interop.BringWindowToTop(targetHwnd);
                return Win32Interop.SetForegroundWindow(targetHwnd);
            }
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr FindOrcaWindow()
    {
        // 1. Check current processes named orca
        Process[] processes = Process.GetProcessesByName("orca");
        try
        {
            foreach (var p in processes)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && Win32Interop.IsWindow(p.MainWindowHandle))
                    {
                        return p.MainWindowHandle;
                    }
                }
                catch
                {
                    // Ignore process access issues
                }
            }
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }

        // 2. Iterate top-level windows; verify window belongs to an "orca" process via PID
        IntPtr foundHwnd = IntPtr.Zero;
        Win32Interop.EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero || !Win32Interop.IsWindowVisible(hwnd)) return true;

            Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return true;

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (string.Equals(proc.ProcessName, "orca", StringComparison.OrdinalIgnoreCase))
                {
                    foundHwnd = hwnd;
                    return false;
                }
            }
            catch
            {
                // Access denied or process exited
            }

            return true;
        }, IntPtr.Zero);

        return foundHwnd;
    }
}
