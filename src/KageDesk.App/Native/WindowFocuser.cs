using System.Diagnostics;
using KageDesk.Core.Collectors;

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

            return FocusHwnd(targetHwnd);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryFocusProcessWindow(int pid)
    {
        if (pid <= 0) return false;

        try
        {
            // 1. Direct check on target PID
            IntPtr directHwnd = FindTopLevelWindowForProcess(pid);
            if (directHwnd != IntPtr.Zero)
            {
                return FocusHwnd(directHwnd);
            }

            // 2. Walk up parent process chain via Win32ProcessInspector to find enclosing terminal window
            var processes = Win32ProcessInspector.Instance.InspectProcesses();
            var parentMap = new Dictionary<int, int>(processes.Count);
            foreach (var p in processes)
            {
                parentMap[p.ProcessId] = p.ParentProcessId;
            }

            int? ancestorPid = Win32ProcessInspector.FindAncestorWithVisibleWindow(
                pid,
                parentMap,
                pId => FindTopLevelWindowForProcess(pId) != IntPtr.Zero,
                maxDepth: 10);

            if (ancestorPid.HasValue)
            {
                IntPtr ancHwnd = FindTopLevelWindowForProcess(ancestorPid.Value);
                if (ancHwnd != IntPtr.Zero)
                {
                    return FocusHwnd(ancHwnd);
                }
            }
        }
        catch
        {
            // Fallback gracefully
        }

        return false;
    }

    public static bool FocusHwnd(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero || !Win32Interop.IsWindow(targetHwnd))
        {
            return false;
        }

        try
        {
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

    private static IntPtr FindTopLevelWindowForProcess(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.MainWindowHandle != IntPtr.Zero &&
                Win32Interop.IsWindow(proc.MainWindowHandle) &&
                Win32Interop.IsWindowVisible(proc.MainWindowHandle))
            {
                return proc.MainWindowHandle;
            }
        }
        catch
        {
            // Ignore access denied or exited
        }

        IntPtr foundHwnd = IntPtr.Zero;
        Win32Interop.EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero || !Win32Interop.IsWindowVisible(hwnd)) return true;

            Win32Interop.GetWindowThreadProcessId(hwnd, out uint winPid);
            if (winPid == (uint)pid)
            {
                foundHwnd = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return foundHwnd;
    }

    private static IntPtr FindOrcaWindow()
    {
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
            }

            return true;
        }, IntPtr.Zero);

        return foundHwnd;
    }
}
