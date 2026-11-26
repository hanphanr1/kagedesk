using System.Diagnostics;
using System.Text;

namespace KageDesk.App.Native;

/// <summary>
/// Safe registered Win32 window-message signaling for Single-Instance Wake-Up UX.
/// When a second KageDesk GUI instance starts, it locates the running instance's hidden
/// UiDispatcherForm (verifying window title &amp; same-process-name ownership) and posts
/// RegisterWindowMessageW("KageDesk.WakeUp.v1") so the running instance surfaces &amp; pulses.
/// </summary>
public static class SingleInstanceWakeUp
{
    public const string WakeUpMessageName = "KageDesk.WakeUp.v1";
    public const string DispatcherWindowTitle = "KageDesk.Dispatcher.v1";

    private static readonly uint CachedMessageId = Win32Interop.RegisterWindowMessage(WakeUpMessageName);

    public static uint MessageId => CachedMessageId;

    public static bool IsWakeUpMessage(int msg)
    {
        return CachedMessageId != 0 && msg == (int)CachedMessageId;
    }

    public static bool TrySignalRunningInstance(IntPtr? explicitTargetHwnd = null)
    {
        if (CachedMessageId == 0)
        {
            return false;
        }

        IntPtr targetHwnd = explicitTargetHwnd ?? FindRunningDispatcherWindow();
        if (targetHwnd == IntPtr.Zero || !Win32Interop.IsWindow(targetHwnd))
        {
            return false;
        }

        return Win32Interop.PostMessage(
            targetHwnd,
            CachedMessageId,
            new IntPtr(Environment.ProcessId),
            IntPtr.Zero);
    }

    public static IntPtr FindRunningDispatcherWindow(uint? excludePid = null)
    {
        uint selfPid = excludePid ?? (uint)Environment.ProcessId;
        string? ownProcName = TryGetProcessNameSafe((int)selfPid);
        IntPtr found = IntPtr.Zero;
        var titleBuf = new StringBuilder(128);

        Win32Interop.EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero) return true;

            titleBuf.Clear();
            int len = Win32Interop.GetWindowText(hwnd, titleBuf, titleBuf.Capacity);
            if (len <= 0 || !string.Equals(titleBuf.ToString(), DispatcherWindowTitle, StringComparison.Ordinal))
            {
                return true;
            }

            Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == selfPid)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(ownProcName))
            {
                string? candidateName = TryGetProcessNameSafe((int)pid);
                if (!string.IsNullOrEmpty(candidateName) &&
                    !string.Equals(candidateName, ownProcName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(candidateName, "KageDesk", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            found = hwnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private static string? TryGetProcessNameSafe(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
