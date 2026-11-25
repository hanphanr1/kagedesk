using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KageDesk.Core.Collectors;

public record ProcessItem(
    int ProcessId,
    int ParentProcessId,
    string ExeName,
    string? CommandLine,
    long StartTimeTicks,
    long TotalCpuTimeMs
);

public interface IProcessInspectionProvider
{
    IReadOnlyList<ProcessItem> InspectProcesses();
}

public sealed class Win32ProcessInspector : IProcessInspectionProvider
{
    public static readonly Win32ProcessInspector Instance = new();

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int STATUS_SUCCESS = 0;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);
    private const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        IntPtr hProcess,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpCreationTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpExitTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    // Cache command line by (PID, StartTimeTicks)
    private readonly Dictionary<(int Pid, long StartTimeTicks), string> _cmdLineCache = new();
    private readonly object _lock = new();

    public IReadOnlyList<ProcessItem> InspectProcesses()
    {
        var rawEntries = new List<(int Pid, int ParentPid, string ExeName)>(256);

        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return Array.Empty<ProcessItem>();
        }

        try
        {
            var entry = new PROCESSENTRY32W();
            entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>();

            if (Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    int pid = (int)entry.th32ProcessID;
                    if (pid > 0)
                    {
                        rawEntries.Add((pid, (int)entry.th32ParentProcessID, entry.szExeFile));
                    }
                }
                while (Process32NextW(snapshot, ref entry));
            }
        }
        catch
        {
            // Fallback gracefully
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var results = new List<ProcessItem>(rawEntries.Count);
        var activeKeys = new HashSet<(int Pid, long StartTimeTicks)>(rawEntries.Count);

        lock (_lock)
        {
            foreach (var (pid, parentPid, exeName) in rawEntries)
            {
                if (!IsRelevantExecutable(exeName))
                {
                    results.Add(new ProcessItem(pid, parentPid, exeName, null, 0, 0));
                    continue;
                }

                IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProcess == IntPtr.Zero)
                {
                    results.Add(new ProcessItem(pid, parentPid, exeName, null, 0, 0));
                    continue;
                }

                try
                {
                    long startTicks = 0;
                    long totalCpuMs = 0;

                    if (GetProcessTimes(hProcess, out var createTime, out _, out var kernelTime, out var userTime))
                    {
                        startTicks = ((long)createTime.dwHighDateTime << 32) | (uint)createTime.dwLowDateTime;
                        long kTicks = ((long)kernelTime.dwHighDateTime << 32) | (uint)kernelTime.dwLowDateTime;
                        long uTicks = ((long)userTime.dwHighDateTime << 32) | (uint)userTime.dwLowDateTime;
                        totalCpuMs = (kTicks + uTicks) / 10000; // 100-ns ticks to ms
                    }

                    var key = (pid, startTicks);
                    activeKeys.Add(key);

                    if (!_cmdLineCache.TryGetValue(key, out string? cmdLine))
                    {
                        cmdLine = ReadProcessCommandLine(hProcess);
                        if (!string.IsNullOrEmpty(cmdLine))
                        {
                            _cmdLineCache[key] = cmdLine;
                        }
                    }

                    results.Add(new ProcessItem(pid, parentPid, exeName, cmdLine, startTicks, totalCpuMs));
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }

            // Evict exited PIDs from command line cache
            if (_cmdLineCache.Count > activeKeys.Count)
            {
                var staleKeys = new List<(int Pid, long StartTimeTicks)>();
                foreach (var k in _cmdLineCache.Keys)
                {
                    if (!activeKeys.Contains(k))
                    {
                        staleKeys.Add(k);
                    }
                }
                foreach (var k in staleKeys)
                {
                    _cmdLineCache.Remove(k);
                }
            }
        }

        return results;
    }

    private static string? ReadProcessCommandLine(IntPtr hProcess)
    {
        int bufSize = 2048;
        IntPtr pBuf = Marshal.AllocHGlobal(bufSize);
        try
        {
            int status = NtQueryInformationProcess(hProcess, ProcessCommandLineInformation, pBuf, bufSize, out int retLen);
            if (status == STATUS_INFO_LENGTH_MISMATCH || status == STATUS_BUFFER_TOO_SMALL || status == STATUS_BUFFER_OVERFLOW)
            {
                Marshal.FreeHGlobal(pBuf);
                bufSize = Math.Max(retLen, bufSize * 2);
                pBuf = Marshal.AllocHGlobal(bufSize);
                status = NtQueryInformationProcess(hProcess, ProcessCommandLineInformation, pBuf, bufSize, out retLen);
            }

            if (status != STATUS_SUCCESS || retLen < Marshal.SizeOf<UNICODE_STRING>())
            {
                return null;
            }

            var ustr = Marshal.PtrToStructure<UNICODE_STRING>(pBuf);
            if (ustr.Length == 0 || ustr.Buffer == IntPtr.Zero)
            {
                return null;
            }

            int charCount = ustr.Length / 2;
            long bufStart = pBuf.ToInt64();
            long strStart = ustr.Buffer.ToInt64();
            long bufEnd = bufStart + retLen;

            // Verify pointer falls within the returned buffer for security
            if (strStart >= bufStart && (strStart + ustr.Length) <= bufEnd)
            {
                return Marshal.PtrToStringUni(ustr.Buffer, charCount);
            }

            // Fallback: characters immediately follow UNICODE_STRING
            IntPtr fallbackPtr = IntPtr.Add(pBuf, Marshal.SizeOf<UNICODE_STRING>());
            if (fallbackPtr.ToInt64() + ustr.Length <= bufEnd)
            {
                return Marshal.PtrToStringUni(fallbackPtr, charCount);
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pBuf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pBuf);
            }
        }
    }

    public static int? FindAncestorWithVisibleWindow(
        int targetPid,
        IReadOnlyDictionary<int, int> parentMap,
        Func<int, bool> hasVisibleWindow,
        int maxDepth = 10)
    {
        if (targetPid <= 0) return null;

        var visited = new HashSet<int> { targetPid };
        int currentPid = targetPid;

        for (int depth = 0; depth < maxDepth; depth++)
        {
            if (hasVisibleWindow(currentPid))
            {
                return currentPid;
            }

            if (!parentMap.TryGetValue(currentPid, out int parentPid) || parentPid <= 0 || !visited.Add(parentPid))
            {
                break;
            }

            currentPid = parentPid;
        }

        return null;
    }

    public static bool IsRelevantExecutable(string exeName)
    {
        if (string.IsNullOrEmpty(exeName)) return false;
        string name = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exeName[..^4].ToLowerInvariant()
            : exeName.ToLowerInvariant();

        switch (name)
        {
            case "omp":
            case "agy":
            case "antigravity":
            case "claude":
            case "codex":
            case "opencode":
            case "gemini":
            case "node":
            case "bun":
            case "git":
            case "gh":
            case "dotnet":
            case "msbuild":
            case "csc":
            case "cargo":
            case "rustc":
            case "tsc":
            case "esbuild":
            case "vite":
            case "gcc":
            case "clang":
            case "rg":
            case "grep":
            case "findstr":
            case "fd":
            case "pwsh":
            case "powershell":
            case "cmd":
            case "bash":
            case "sh":
            case "python":
                return true;
        }

        if (name.StartsWith("npm", StringComparison.Ordinal) ||
            name.StartsWith("pnpm", StringComparison.Ordinal) ||
            name.StartsWith("vstest", StringComparison.Ordinal) ||
            name.StartsWith("testhost", StringComparison.Ordinal) ||
            name.StartsWith("pytest", StringComparison.Ordinal) ||
            name.StartsWith("vitest", StringComparison.Ordinal) ||
            name.StartsWith("jest", StringComparison.Ordinal) ||
            name.StartsWith("python", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}
