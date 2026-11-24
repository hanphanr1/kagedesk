using System.Diagnostics;
using System.Text;

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
                Win32Interop.AttachThreadInput(curThread, fgThread, true);
                Win32Interop.BringWindowToTop(targetHwnd);
                Win32Interop.SetForegroundWindow(targetHwnd);
                Win32Interop.AttachThreadInput(curThread, fgThread, false);
            }
            else
            {
                Win32Interop.BringWindowToTop(targetHwnd);
                Win32Interop.SetForegroundWindow(targetHwnd);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr FindOrcaWindow()
    {
        // 1. Check current processes named orca
        var processes = Process.GetProcessesByName("orca");
        foreach (var p in processes)
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    return p.MainWindowHandle;
                }
            }
            catch
            {
                // Ignore process access issues
            }
            finally
            {
                p.Dispose();
            }
        }

        // 2. Iterate top-level windows for titles matching "Orca"
        IntPtr foundHwnd = IntPtr.Zero;
        var sb = new StringBuilder(512);

        Win32Interop.EnumWindows((hwnd, _) =>
        {
            sb.Clear();
            int len = Win32Interop.GetWindowText(hwnd, sb, sb.Capacity);
            if (len > 0)
            {
                string title = sb.ToString();
                if (title.Contains("Orca", StringComparison.OrdinalIgnoreCase))
                {
                    foundHwnd = hwnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        return foundHwnd;
    }
}
