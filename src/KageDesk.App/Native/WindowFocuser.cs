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

    public static bool TryFocusProcessWindow(int pid, long? expectedStartTicks = null)
    {
        return TryFocusProcessWindowCore(
            pid,
            expectedStartTicks,
            getStartTime: GetLiveProcessStartTimeTicks,
            getAncestry: Win32ProcessInspector.GetProcessAncestrySnapshot,
            findWindow: FindTopLevelWindowForProcess,
            verifyOwnership: (h, p, t) => VerifyHwndOwnership(h, p, t),
            focusWindow: FocusHwnd);
    }

    public static bool TryFocusProcessWindowCore(
        int pid,
        long? expectedStartTicks = null,
        Func<int, long?>? getStartTime = null,
        Func<IReadOnlyDictionary<int, int>>? getAncestry = null,
        Func<int, IntPtr>? findWindow = null,
        Func<IntPtr, int, long, bool>? verifyOwnership = null,
        Func<IntPtr, bool>? focusWindow = null)
    {
        if (pid <= 0) return false;

        getStartTime ??= GetLiveProcessStartTimeTicks;
        getAncestry ??= Win32ProcessInspector.GetProcessAncestrySnapshot;
        findWindow ??= FindTopLevelWindowForProcess;
        verifyOwnership ??= (h, p, t) => VerifyHwndOwnership(h, p, t);
        focusWindow ??= FocusHwnd;

        try
        {
            long? targetStartTicks = getStartTime(pid);
            if (!targetStartTicks.HasValue) return false;

            if (expectedStartTicks.HasValue && !IsPidAgeValid(targetStartTicks.Value, expectedStartTicks.Value))
            {
                return false;
            }

            long verifiedTargetTicks = targetStartTicks.Value;

            // 1. Direct check on target PID
            IntPtr directHwnd = findWindow(pid);
            if (directHwnd != IntPtr.Zero && verifyOwnership(directHwnd, pid, verifiedTargetTicks))
            {
                return focusWindow(directHwnd);
            }

            // 2. Walk up parent process chain via lightweight GetProcessAncestrySnapshot
            var parentMap = getAncestry();

            // Immediately after capturing ancestry snapshot, re-verify starting PID's live StartTimeUtcTicks
            long? postSnapshotStartTicks = getStartTime(pid);
            if (!postSnapshotStartTicks.HasValue || postSnapshotStartTicks.Value != verifiedTargetTicks)
            {
                return false;
            }

            int currentPid = pid;
            long currentStartTicks = verifiedTargetTicks;
            var visited = new HashSet<int> { pid };

            for (int depth = 0; depth < 10; depth++)
            {
                if (!parentMap.TryGetValue(currentPid, out int parentPid) || parentPid <= 0)
                {
                    break;
                }

                // Cycle protection
                if (!visited.Add(parentPid))
                {
                    break;
                }

                // On EVERY hop child -> parent, re-verify the child's live StartTimeUtcTicks matches previous verified hop's start ticks
                long? liveChildTicks = getStartTime(currentPid);
                if (!liveChildTicks.HasValue || liveChildTicks.Value != currentStartTicks)
                {
                    break;
                }

                // Verify parentStartTicks > 0 && parentStartTicks <= childStartTicks
                long? parentStartTicks = getStartTime(parentPid);
                if (!parentStartTicks.HasValue || parentStartTicks.Value <= 0 || !IsParentAgeValid(parentStartTicks.Value, currentStartTicks))
                {
                    break;
                }

                // Before activating any HWND, re-verify IsWindow && IsWindowVisible and GetWindowThreadProcessId + StartTimeUtcTicks match
                IntPtr parentHwnd = findWindow(parentPid);
                if (parentHwnd != IntPtr.Zero && verifyOwnership(parentHwnd, parentPid, parentStartTicks.Value))
                {
                    return focusWindow(parentHwnd);
                }

                currentPid = parentPid;
                currentStartTicks = parentStartTicks.Value;
            }
        }
        catch
        {
            // Fallback gracefully
        }

        return false;
    }

    public static bool IsPidAgeValid(long liveStartTicks, long expectedStartTicks) =>
        liveStartTicks == expectedStartTicks;

    public static bool IsParentAgeValid(long parentStartTicks, long childStartTicks) =>
        parentStartTicks <= childStartTicks;

    public static bool VerifyHwndOwnership(
        IntPtr hwnd,
        int expectedPid,
        long expectedStartTicks,
        Func<int, long?>? getStartTime = null,
        Func<IntPtr, uint>? getOwnerPid = null,
        Func<IntPtr, bool>? isWindowValid = null)
    {
        if (hwnd == IntPtr.Zero) return false;

        isWindowValid ??= h => Win32Interop.IsWindow(h) && Win32Interop.IsWindowVisible(h);
        if (!isWindowValid(hwnd)) return false;

        getOwnerPid ??= h => { Win32Interop.GetWindowThreadProcessId(h, out uint p); return p; };
        uint owner = getOwnerPid(hwnd);
        if (owner != (uint)expectedPid) return false;

        getStartTime ??= GetLiveProcessStartTimeTicks;
        long? liveTicks = getStartTime(expectedPid);
        if (!liveTicks.HasValue || liveTicks.Value != expectedStartTicks) return false;

        return true;
    }

    public static long? GetLiveProcessStartTimeTicks(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return null;
        }
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
