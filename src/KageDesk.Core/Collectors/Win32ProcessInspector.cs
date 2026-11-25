using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KageDesk.Core.Collectors;

public record ProcessClassification(
    string? Provider,
    bool IsInternalDaemon,
    bool IsElectronHelper,
    string? RoleHint
);

public record ProcessItem(
    int ProcessId,
    int ParentProcessId,
    string ExeName,
    string? CommandLine,
    long StartTimeTicks,
    long TotalCpuTimeMs,
    ProcessClassification? Classification = null
);

public interface IProcessInspectionProvider
{
    IReadOnlyList<ProcessItem> InspectProcesses();
    void ClearCache() { }
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
    internal struct UNICODE_STRING
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

    // Cache ONLY derived ProcessClassification by (PID, StartTimeTicks)
    private readonly Dictionary<(int Pid, long StartTimeTicks), ProcessClassification> _classificationCache = new();
    private readonly object _lock = new();
    private long _cacheGeneration;

    public long CacheGeneration => Interlocked.Read(ref _cacheGeneration);

    public int CacheCount
    {
        get
        {
            lock (_lock)
            {
                return _classificationCache.Count;
            }
        }
    }

    public void ClearCache()
    {
        lock (_lock)
        {
            Interlocked.Increment(ref _cacheGeneration);
            _classificationCache.Clear();
        }
    }

    public static bool RequiresCommandLineClassification(string exeName)
    {
        if (string.IsNullOrEmpty(exeName)) return false;
        string name = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exeName[..^4].ToLowerInvariant()
            : exeName.ToLowerInvariant();

        return name is "node" or "bun" or "omp" or "agy" or "antigravity" or "claude" or "codex" or "opencode" or "gemini";
    }

    internal bool TryGetCachedClassification(int pid, long startTicks, out ProcessClassification? classification)
    {
        lock (_lock)
        {
            return _classificationCache.TryGetValue((pid, startTicks), out classification);
        }
    }

    internal ProcessClassification ResolveClassification(
        int pid,
        long startTicks,
        string exeName,
        Func<string?> readCommandLine,
        long capturedGen)
    {
        if (startTicks <= 0)
        {
            // Never cache when startTicks <= 0
            string? cmdLine = readCommandLine();
            return ClassifyProcess(exeName, cmdLine);
        }

        var key = (pid, startTicks);
        lock (_lock)
        {
            if (_classificationCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        string? cmd = readCommandLine();

        if (cmd == null)
        {
            // Exe-only fallback for THIS poll; do NOT poison cache for ANY process!
            return ClassifyProcess(exeName, null);
        }

        var classification = ClassifyProcess(exeName, cmd);

        lock (_lock)
        {
            if (Interlocked.Read(ref _cacheGeneration) == capturedGen)
            {
                _classificationCache[key] = classification;
            }
        }

        return classification;
    }

    public static Dictionary<int, int> GetProcessAncestrySnapshot()
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return new Dictionary<int, int>();
        }

        var parentMap = new Dictionary<int, int>(256);
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
                        parentMap[pid] = (int)entry.th32ParentProcessID;
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

        return parentMap;
    }

    public IReadOnlyList<ProcessItem> InspectProcesses()
    {
        long capturedGen = Interlocked.Read(ref _cacheGeneration);
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

        foreach (var (pid, parentPid, exeName) in rawEntries)
        {
            if (!IsRelevantExecutable(exeName))
            {
                results.Add(new ProcessItem(pid, parentPid, exeName, null, 0, 0, null));
                continue;
            }

            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                results.Add(new ProcessItem(pid, parentPid, exeName, null, 0, 0, null));
                continue;
            }

            try
            {
                long startTicks = 0;
                long totalCpuMs = 0;

                if (GetProcessTimes(hProcess, out var createTime, out _, out var kernelTime, out var userTime))
                {
                    long ft = ((long)createTime.dwHighDateTime << 32) | (uint)createTime.dwLowDateTime;
                    try
                    {
                        startTicks = DateTime.FromFileTimeUtc(ft).Ticks;
                    }
                    catch
                    {
                        startTicks = ft;
                    }
                    long kTicks = ((long)kernelTime.dwHighDateTime << 32) | (uint)kernelTime.dwLowDateTime;
                    long uTicks = ((long)userTime.dwHighDateTime << 32) | (uint)userTime.dwLowDateTime;
                    totalCpuMs = (kTicks + uTicks) / 10000; // 100-ns ticks to ms
                }

                if (startTicks > 0)
                {
                    activeKeys.Add((pid, startTicks));
                }

                var classification = ResolveClassification(
                    pid,
                    startTicks,
                    exeName,
                    () => ReadProcessCommandLine(hProcess),
                    capturedGen);

                results.Add(new ProcessItem(pid, parentPid, exeName, null, startTicks, totalCpuMs, classification));
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        lock (_lock)
        {
            if (Interlocked.Read(ref _cacheGeneration) == capturedGen)
            {
                var staleKeys = new List<(int Pid, long StartTimeTicks)>();
                foreach (var k in _classificationCache.Keys)
                {
                    if (!activeKeys.Contains(k))
                    {
                        staleKeys.Add(k);
                    }
                }
                foreach (var k in staleKeys)
                {
                    _classificationCache.Remove(k);
                }
            }
        }

        return results;
    }

    private static string? ReadProcessCommandLine(IntPtr hProcess)
    {
        int capacity = 2048;
        const int maxCapacity = 65536; // 64 KB cap
        int attempts = 0;

        while (attempts < 4)
        {
            attempts++;
            IntPtr pBuf = IntPtr.Zero;
            try
            {
                pBuf = Marshal.AllocHGlobal(capacity);
                int status = NtQueryInformationProcess(hProcess, ProcessCommandLineInformation, pBuf, capacity, out int retLen);

                if (status == STATUS_INFO_LENGTH_MISMATCH || status == STATUS_BUFFER_TOO_SMALL || status == STATUS_BUFFER_OVERFLOW)
                {
                    if (capacity >= maxCapacity)
                    {
                        return null;
                    }
                    capacity = Math.Clamp(Math.Max(retLen, capacity * 2), capacity + 1, maxCapacity);
                    continue;
                }

                if (status != STATUS_SUCCESS)
                {
                    return null;
                }

                return TryDecodeUnicodeStringFromBuffer(pBuf, retLen, capacity);
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

        return null;
    }

    internal static string? TryDecodeUnicodeStringFromBuffer(IntPtr pBuf, int retLen, int capacity)
    {
        if (pBuf == IntPtr.Zero) return null;

        int headerSize = Marshal.SizeOf<UNICODE_STRING>();
        if (headerSize > retLen || retLen > capacity)
        {
            return null;
        }

        UNICODE_STRING ustr;
        try
        {
            ustr = Marshal.PtrToStructure<UNICODE_STRING>(pBuf);
        }
        catch
        {
            return null;
        }

        ushort length = ustr.Length;
        ushort maxLen = ustr.MaximumLength;
        IntPtr unicodeBufPtr = ustr.Buffer;

        if ((length & 1) != 0 || length > maxLen)
        {
            return null;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        if (unicodeBufPtr == IntPtr.Zero)
        {
            return null;
        }

        ulong baseAddr = (ulong)pBuf;
        ulong payloadStart = baseAddr + (ulong)headerSize;
        ulong payloadEnd = baseAddr + (ulong)retLen;
        ulong strAddr = (ulong)unicodeBufPtr;

        if (strAddr < payloadStart || strAddr > payloadEnd || (ulong)length > (payloadEnd - strAddr))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(unicodeBufPtr, length / 2);
        }
        catch
        {
            return null;
        }
    }

    public static ProcessClassification ClassifyProcess(string exeName, string? cmdLine)
    {
        if (string.IsNullOrEmpty(exeName))
        {
            return new ProcessClassification(null, false, false, null);
        }

        string name = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exeName[..^4].ToLowerInvariant()
            : exeName.ToLowerInvariant();
        string cmd = cmdLine?.ToLowerInvariant() ?? string.Empty;

        bool isInternalDaemon = cmd.Contains("__omp_worker_");
        bool isElectronHelper = cmd.Contains("--type=") ||
                                cmd.Contains("--crashpad") ||
                                cmd.Contains("crashpad-handler");

        string? provider = null;

        if (name is "omp")
        {
            provider = "OMP";
        }
        else if (name is "agy" or "antigravity")
        {
            provider = "AGY";
        }
        else if (name is "claude")
        {
            provider = "CLAUDE";
        }
        else if (name is "codex")
        {
            provider = "CODEX";
        }
        else if (name is "opencode")
        {
            provider = "OPENCODE";
        }
        else if (name is "gemini")
        {
            provider = "GEMINI";
        }
        else if (name is "node" or "bun")
        {
            if (cmd.Contains("claude-code") || cmd.Contains("@anthropic-ai/claude") ||
                cmd.Contains("\\claude.js") || cmd.Contains("/claude.js") ||
                cmd.Contains("/claude") || cmd.Contains("\\claude"))
            {
                provider = "CLAUDE";
            }
            else if (cmd.Contains("@openai/codex") || cmd.Contains("\\codex") || cmd.Contains("/codex"))
            {
                provider = "CODEX";
            }
            else if (cmd.Contains("opencode"))
            {
                provider = "OPENCODE";
            }
            else if (cmd.Contains("gemini-cli") || cmd.Contains("@google/gemini"))
            {
                provider = "GEMINI";
            }
        }

        string? roleHint = null;
        if (name is "git" or "gh")
        {
            roleHint = "git";
        }
        else if (name.StartsWith("vstest", StringComparison.Ordinal) ||
                 name.StartsWith("testhost", StringComparison.Ordinal) ||
                 name.StartsWith("pytest", StringComparison.Ordinal) ||
                 name.StartsWith("vitest", StringComparison.Ordinal) ||
                 name.StartsWith("jest", StringComparison.Ordinal) ||
                 cmd.Contains("pytest") || cmd.Contains("vitest") || cmd.Contains("jest"))
        {
            roleHint = "test";
        }
        else if (name is "dotnet" or "msbuild" or "csc" or "cargo" or "rustc" or "gcc" or "clang" or "tsc" or "esbuild" or "vite" ||
                 name.StartsWith("npm", StringComparison.Ordinal) ||
                 name.StartsWith("pnpm", StringComparison.Ordinal) ||
                 cmd.Contains("dotnet build") || cmd.Contains("msbuild") || cmd.Contains("cargo build"))
        {
            roleHint = "build";
        }
        else if (name is "rg" or "grep" or "findstr" or "fd")
        {
            roleHint = "search";
        }
        else if (name is "pwsh" or "powershell" or "cmd" or "bash" or "sh" or "python" or "node")
        {
            roleHint = "execute";
        }

        return new ProcessClassification(provider, isInternalDaemon, isElectronHelper, roleHint);
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
