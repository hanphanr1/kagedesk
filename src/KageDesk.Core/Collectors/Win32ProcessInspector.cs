using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KageDesk.Core.Collectors;

public record ProcessClassification(
    string? Provider,
    bool IsInternalDaemon,
    bool IsElectronHelper,
    string? RoleHint,
    bool IsPersistentDaemon = false
);

public record ProcessItem(
    int ProcessId,
    int ParentProcessId,
    string ExeName,
    string? CommandLine,
    long StartTimeTicks,
    long TotalCpuTimeMs,
    ProcessClassification? Classification = null,
    bool HasIoCounters = false,
    long ReadTransferBytes = 0,
    long OtherTransferBytes = 0
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
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    private static readonly ProcessClassification RejectedHandleClassification = new(null, false, false, null);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(
        IntPtr hProcess,
        out IO_COUNTERS lpIoCounters);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess,
        uint dwFlags,
        [Out] char[] lpExeName,
        ref uint lpdwSize);

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

    private readonly Func<long> _utcNowTicksProvider;
    private readonly Func<IReadOnlyList<RawToolhelpEntry>?> _captureRawSnapshot;
    private readonly Func<int, bool, Func<OpenedProcessHandleData, ProcessItem>, ProcessItem?> _withOpenedHandle;

    public Win32ProcessInspector()
        : this(null, null, null)
    {
    }

    internal Win32ProcessInspector(
        Func<long>? utcNowTicksProvider = null,
        Func<IReadOnlyList<RawToolhelpEntry>?>? captureRawSnapshot = null,
        Func<int, bool, Func<OpenedProcessHandleData, ProcessItem>, ProcessItem?>? withOpenedHandle = null)
    {
        _utcNowTicksProvider = utcNowTicksProvider ?? (static () => DateTime.UtcNow.Ticks);
        _captureRawSnapshot = captureRawSnapshot ?? CaptureWin32RawSnapshot;
        _withOpenedHandle = withOpenedHandle ?? WithWin32ProcessHandle;
    }

    public long CacheGeneration => Volatile.Read(ref _cacheGeneration);

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
            if (Volatile.Read(ref _cacheGeneration) == capturedGen &&
                _classificationCache.TryGetValue(key, out var cached))
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
            if (Volatile.Read(ref _cacheGeneration) == capturedGen)
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

    internal readonly record struct RawToolhelpEntry(int Pid, int ParentPid, string ExeName);

    internal readonly record struct OpenedProcessHandleData(
        long StartTimeTicks,
        long TotalCpuTimeMs,
        string? ImagePath,
        Func<(bool HasIoCounters, long ReadTransferBytes, long OtherTransferBytes)> ReadIoCounters,
        Func<string?> ReadCommandLine);

    internal static string? ExtractExecutableBasename(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        string trimmed = imagePath.Trim();
        int lastSlash = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        string fileName = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    public static bool VerifyProcessHandleIdentity(
        string? toolhelpExeName,
        long snapshotCutoffUtcTicks,
        long handleCreationTimeTicks,
        string? handleImagePath)
    {
        if (handleCreationTimeTicks <= 0 ||
            snapshotCutoffUtcTicks <= 0 ||
            handleCreationTimeTicks >= snapshotCutoffUtcTicks)
        {
            return false;
        }

        string? expectedBasename = ExtractExecutableBasename(toolhelpExeName);
        string? actualBasename = ExtractExecutableBasename(handleImagePath);
        if (string.IsNullOrEmpty(expectedBasename) || string.IsNullOrEmpty(actualBasename))
        {
            return false;
        }

        return string.Equals(expectedBasename, actualBasename, StringComparison.OrdinalIgnoreCase);
    }

    private static long TryConvertFileTimeToUtcTicks(System.Runtime.InteropServices.ComTypes.FILETIME fileTime)
    {
        long ft = ((long)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;
        if (ft <= 0)
        {
            return 0;
        }

        try
        {
            return DateTime.FromFileTimeUtc(ft).Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private static string? QueryProcessImagePath(IntPtr hProcess)
    {
        uint capacity = 1024;
        const uint maxCapacity = 32768;

        while (capacity <= maxCapacity)
        {
            var buffer = new char[capacity];
            uint size = capacity;
            if (QueryFullProcessImageNameW(hProcess, 0, buffer, ref size))
            {
                if (size == 0 || size > buffer.Length)
                {
                    return null;
                }
                return new string(buffer, 0, (int)size);
            }

            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_INSUFFICIENT_BUFFER || capacity >= maxCapacity)
            {
                return null;
            }

            capacity = Math.Min(capacity * 2, maxCapacity);
        }

        return null;
    }

    public IReadOnlyList<ProcessItem> InspectProcesses()
    {
        long capturedGeneration = Volatile.Read(ref _cacheGeneration);
        long snapshotCutoffUtcTicks = _utcNowTicksProvider();

        var rawEntries = _captureRawSnapshot();
        if (rawEntries == null)
        {
            return Array.Empty<ProcessItem>();
        }

        return InspectFromRawSnapshot(rawEntries, snapshotCutoffUtcTicks, capturedGeneration, _withOpenedHandle);
    }

    private static IReadOnlyList<RawToolhelpEntry>? CaptureWin32RawSnapshot()
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return null;
        }

        var rawEntries = new List<RawToolhelpEntry>(256);
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
                        rawEntries.Add(new RawToolhelpEntry(pid, (int)entry.th32ParentProcessID, entry.szExeFile));
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

        return rawEntries;
    }

    private static T? WithWin32ProcessHandle<T>(int pid, bool includeCpuTime, Func<OpenedProcessHandleData, T> onHandle)
    {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            return default;
        }

        try
        {
            long startTicks = 0;
            long totalCpuMs = 0;
            if (GetProcessTimes(hProcess, out var createTime, out _, out var kernelTime, out var userTime))
            {
                startTicks = TryConvertFileTimeToUtcTicks(createTime);
                if (includeCpuTime)
                {
                    long kTicks = ((long)kernelTime.dwHighDateTime << 32) | (uint)kernelTime.dwLowDateTime;
                    long uTicks = ((long)userTime.dwHighDateTime << 32) | (uint)userTime.dwLowDateTime;
                    totalCpuMs = (kTicks + uTicks) / 10000; // 100-ns ticks to ms
                }
            }

            string? imagePath = QueryProcessImagePath(hProcess);

            var data = new OpenedProcessHandleData(
                StartTimeTicks: startTicks,
                TotalCpuTimeMs: totalCpuMs,
                ImagePath: imagePath,
                ReadIoCounters: () =>
                {
                    if (GetProcessIoCounters(hProcess, out var ioCounters))
                    {
                        long readBytes = ioCounters.ReadTransferCount > long.MaxValue ? long.MaxValue : (long)ioCounters.ReadTransferCount;
                        long otherBytes = ioCounters.OtherTransferCount > long.MaxValue ? long.MaxValue : (long)ioCounters.OtherTransferCount;
                        return (true, readBytes, otherBytes);
                    }
                    return (false, 0L, 0L);
                },
                ReadCommandLine: () => ReadProcessCommandLine(hProcess));

            return onHandle(data);
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    internal IReadOnlyList<ProcessItem> InspectFromRawSnapshot(
        IReadOnlyList<RawToolhelpEntry> rawEntries,
        long snapshotCutoffUtcTicks,
        long capturedGeneration,
        Func<int, bool, Func<OpenedProcessHandleData, ProcessItem>, ProcessItem?> withOpenedHandle)
    {
        var rawParentByPid = new Dictionary<int, int>(rawEntries.Count);
        var rawChildrenByPid = new Dictionary<int, List<int>>(rawEntries.Count);
        var relevantOrOrcaPids = new HashSet<int>();
        var orcaSeedPids = new Queue<int>();

        for (int i = 0; i < rawEntries.Count; i++)
        {
            var entry = rawEntries[i];
            int pid = entry.Pid;
            int parentPid = entry.ParentPid;
            string exeName = entry.ExeName;

            rawParentByPid[pid] = parentPid;
            if (!rawChildrenByPid.TryGetValue(parentPid, out var childList))
            {
                childList = new List<int>();
                rawChildrenByPid[parentPid] = childList;
            }
            childList.Add(pid);

            if (IsOrcaExecutable(exeName))
            {
                relevantOrOrcaPids.Add(pid);
                orcaSeedPids.Enqueue(pid);
            }
            else if (IsRelevantExecutable(exeName))
            {
                relevantOrOrcaPids.Add(pid);
            }
        }

        // Collect all PIDs that participate in Orca descendant trees or parent chains of relevant executables
        var ancestryTimePids = new HashSet<int>(relevantOrOrcaPids);
        while (orcaSeedPids.Count > 0)
        {
            int cur = orcaSeedPids.Dequeue();
            if (rawChildrenByPid.TryGetValue(cur, out var children))
            {
                for (int i = 0; i < children.Count; i++)
                {
                    if (ancestryTimePids.Add(children[i]))
                    {
                        orcaSeedPids.Enqueue(children[i]);
                    }
                }
            }
        }

        foreach (var pid in relevantOrOrcaPids)
        {
            int cur = pid;
            for (int depth = 0; depth < 16; depth++)
            {
                if (!rawParentByPid.TryGetValue(cur, out int pPid) || pPid <= 0)
                {
                    break;
                }
                if (!ancestryTimePids.Add(pPid))
                {
                    break;
                }
                cur = pPid;
            }
        }

        var results = new List<ProcessItem>(rawEntries.Count);
        var activeKeys = new HashSet<(int Pid, long StartTimeTicks)>(rawEntries.Count);

        for (int i = 0; i < rawEntries.Count; i++)
        {
            var entry = rawEntries[i];
            int pid = entry.Pid;
            int parentPid = entry.ParentPid;
            string exeName = entry.ExeName;

            if (!IsRelevantExecutable(exeName))
            {
                ProcessItem? ancestryItem = null;
                if (ancestryTimePids.Contains(pid))
                {
                    ancestryItem = withOpenedHandle(pid, false, data =>
                    {
                        // Verify handle identity on the SAME opened handle (creationTime < pre-call cutoff && basename matches szExeFile)
                        bool verified = VerifyProcessHandleIdentity(
                            exeName,
                            snapshotCutoffUtcTicks,
                            data.StartTimeTicks,
                            data.ImagePath);

                        long verifiedStartTicks = verified ? data.StartTimeTicks : 0;
                        return new ProcessItem(pid, parentPid, exeName, null, verifiedStartTicks, 0, null);
                    });
                }

                results.Add(ancestryItem ?? new ProcessItem(pid, parentPid, exeName, null, 0, 0, null));
                continue;
            }

            ProcessItem? relevantItem = withOpenedHandle(pid, true, data =>
            {
                // Verify handle identity on the SAME opened handle BEFORE trusting StartTimeTicks, metrics, or command line
                if (!VerifyProcessHandleIdentity(exeName, snapshotCutoffUtcTicks, data.StartTimeTicks, data.ImagePath))
                {
                    // PID was reused or handle identity could not be verified: reject handle completely
                    return new ProcessItem(pid, parentPid, exeName, null, 0, 0, RejectedHandleClassification);
                }

                long startTicks = data.StartTimeTicks;
                var (hasIoCounters, readTransferBytes, otherTransferBytes) = data.ReadIoCounters();

                activeKeys.Add((pid, startTicks));

                var classification = ResolveClassification(
                    pid,
                    startTicks,
                    exeName,
                    data.ReadCommandLine,
                    capturedGeneration);

                return new ProcessItem(
                    pid,
                    parentPid,
                    exeName,
                    null,
                    startTicks,
                    data.TotalCpuTimeMs,
                    classification,
                    hasIoCounters,
                    readTransferBytes,
                    otherTransferBytes);
            });

            results.Add(relevantItem ?? new ProcessItem(pid, parentPid, exeName, null, 0, 0, null));
        }

        lock (_lock)
        {
            if (Volatile.Read(ref _cacheGeneration) == capturedGeneration)
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
        bool isPersistentDaemon = isInternalDaemon ||
                                  cmd.Contains("mcp-server") ||
                                  cmd.Contains("mcp_server") ||
                                  cmd.Contains("@modelcontextprotocol") ||
                                  cmd.Contains("language-server") ||
                                  cmd.Contains("languageserver") ||
                                  cmd.Contains("tsserver") ||
                                  cmd.Contains("pyright") ||
                                  cmd.Contains("rust-analyzer") ||
                                  cmd.Contains("omnisharp") ||
                                  cmd.Contains("clangd") ||
                                  cmd.Contains("--service") ||
                                  (cmd.Contains("mcp") && (name is "node" or "bun" or "python" or "python3" or "uvx" or "npx")) ||
                                  (cmd.Contains("lsp") && (name is "node" or "bun" or "python" or "python3"));

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

        return new ProcessClassification(provider, isInternalDaemon, isElectronHelper, roleHint, isPersistentDaemon);
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

    public static bool IsOrcaExecutable(string exeName)
    {
        return string.Equals(exeName, "Orca.exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(exeName, "orca", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidParentChildEdge(ProcessItem parent, ProcessItem child)
    {
        return parent.StartTimeTicks > 0 &&
               child.StartTimeTicks > 0 &&
               parent.StartTimeTicks <= child.StartTimeTicks;
    }
}
