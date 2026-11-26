using System.Runtime.InteropServices;
using KageDesk.Core.Collectors;
using Xunit;

namespace KageDesk.Core.Tests;

public class Win32ProcessInspectorTests
{
    [Fact]
    public void TryDecodeUnicodeStringFromBuffer_NullPointer_ReturnsNull()
    {
        var result = Win32ProcessInspector.TryDecodeUnicodeStringFromBuffer(IntPtr.Zero, 100, 100);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0, 100)]   // retLen < headerSize
    [InlineData(15, 100)]  // retLen < headerSize (header is 16 bytes on x64)
    [InlineData(101, 100)] // retLen > capacity
    public void TryDecodeUnicodeStringFromBuffer_InvalidRetLen_ReturnsNull(int retLen, int capacity)
    {
        int headerSize = Marshal.SizeOf<Win32ProcessInspector.UNICODE_STRING>();
        int allocSize = Math.Max(retLen, capacity) + 16;
        IntPtr pBuf = Marshal.AllocHGlobal(allocSize);
        try
        {
            var result = Win32ProcessInspector.TryDecodeUnicodeStringFromBuffer(pBuf, retLen, capacity);
            Assert.Null(result);
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }
    }

    [Fact]
    public void TryDecodeUnicodeStringFromBuffer_OddLength_ReturnsNull()
    {
        int headerSize = Marshal.SizeOf<Win32ProcessInspector.UNICODE_STRING>();
        int capacity = 256;
        IntPtr pBuf = Marshal.AllocHGlobal(capacity);
        try
        {
            var ustr = new Win32ProcessInspector.UNICODE_STRING
            {
                Length = 7, // Odd length!
                MaximumLength = 10,
                Buffer = IntPtr.Add(pBuf, headerSize)
            };
            Marshal.StructureToPtr(ustr, pBuf, false);

            var result = Win32ProcessInspector.TryDecodeUnicodeStringFromBuffer(pBuf, headerSize + 32, capacity);
            Assert.Null(result);
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }
    }

    [Fact]
    public void TryDecodeUnicodeStringFromBuffer_LengthGreaterThanMaxLen_ReturnsNull()
    {
        int headerSize = Marshal.SizeOf<Win32ProcessInspector.UNICODE_STRING>();
        int capacity = 256;
        IntPtr pBuf = Marshal.AllocHGlobal(capacity);
        try
        {
            var ustr = new Win32ProcessInspector.UNICODE_STRING
            {
                Length = 20,
                MaximumLength = 10, // Length > MaxLength!
                Buffer = IntPtr.Add(pBuf, headerSize)
            };
            Marshal.StructureToPtr(ustr, pBuf, false);

            var result = Win32ProcessInspector.TryDecodeUnicodeStringFromBuffer(pBuf, headerSize + 32, capacity);
            Assert.Null(result);
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }
    }

    [Fact]
    public void TryDecodeUnicodeStringFromBuffer_ZeroLength_ReturnsEmptyString()
    {
        int headerSize = Marshal.SizeOf<Win32ProcessInspector.UNICODE_STRING>();
        int capacity = 256;
        IntPtr pBuf = Marshal.AllocHGlobal(capacity);
        try
        {
            var ustr = new Win32ProcessInspector.UNICODE_STRING
            {
                Length = 0,
                MaximumLength = 10,
                Buffer = IntPtr.Add(pBuf, headerSize)
            };
            Marshal.StructureToPtr(ustr, pBuf, false);

            var result = Win32ProcessInspector.TryDecodeUnicodeStringFromBuffer(pBuf, headerSize + 32, capacity);
            Assert.Equal(string.Empty, result);
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }
    }

    [Fact]
    public void TryDecodeUnicodeStringFromBuffer_ZeroBufferPtr_ReturnsNull()
    {
        int headerSize = Marshal.SizeOf<Win32ProcessInspector.UNICODE_STRING>();
        int capacity = 256;
        IntPtr pBuf = Marshal.AllocHGlobal(capacity);
        try
        {
            var ustr = new Win32ProcessInspector.UNICODE_STRING
            {
                Length = 10,
                MaximumLength = 20,
                Buffer = IntPtr.Zero // Zero buffer ptr with non-zero length
            };
            Marshal.StructureToPtr(ustr, pBuf, false);

            var result = Win32ProcessInspector.TryDecodeUnicodeStringFromBuffer(pBuf, headerSize + 32, capacity);
            Assert.Null(result);
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }
    }

    [Theory]
    [InlineData(-4)] // Before payloadStart
    [InlineData(100)] // Past payloadEnd
    public void TryDecodeUnicodeStringFromBuffer_OutOfRangeBufferPointer_ReturnsNull(int offsetFromPayloadStart)
    {
        int headerSize = Marshal.SizeOf<Win32ProcessInspector.UNICODE_STRING>();
        int payloadLen = 32;
        int retLen = headerSize + payloadLen;
        int capacity = 256;
        IntPtr pBuf = Marshal.AllocHGlobal(capacity);
        try
        {
            IntPtr bufferPtr = IntPtr.Add(pBuf, headerSize + offsetFromPayloadStart);
            var ustr = new Win32ProcessInspector.UNICODE_STRING
            {
                Length = 10,
                MaximumLength = 20,
                Buffer = bufferPtr
            };
            Marshal.StructureToPtr(ustr, pBuf, false);

            var result = Win32ProcessInspector.TryDecodeUnicodeStringFromBuffer(pBuf, retLen, capacity);
            Assert.Null(result);
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }
    }

    [Fact]
    public void TryDecodeUnicodeStringFromBuffer_LengthExceedingPayloadEnd_ReturnsNull()
    {
        int headerSize = Marshal.SizeOf<Win32ProcessInspector.UNICODE_STRING>();
        int payloadLen = 16;
        int retLen = headerSize + payloadLen;
        int capacity = 256;
        IntPtr pBuf = Marshal.AllocHGlobal(capacity);
        try
        {
            // Buffer starts at headerSize + 10, length is 10 bytes -> ends at headerSize + 20 > headerSize + 16
            IntPtr bufferPtr = IntPtr.Add(pBuf, headerSize + 10);
            var ustr = new Win32ProcessInspector.UNICODE_STRING
            {
                Length = 10,
                MaximumLength = 20,
                Buffer = bufferPtr
            };
            Marshal.StructureToPtr(ustr, pBuf, false);

            var result = Win32ProcessInspector.TryDecodeUnicodeStringFromBuffer(pBuf, retLen, capacity);
            Assert.Null(result);
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }
    }

    [Fact]
    public void TryDecodeUnicodeStringFromBuffer_ValidBuffer_ReturnsDecodedString()
    {
        string expected = "codex --agent -p \"test\"";
        byte[] expectedBytes = System.Text.Encoding.Unicode.GetBytes(expected);

        int headerSize = Marshal.SizeOf<Win32ProcessInspector.UNICODE_STRING>();
        int payloadLen = expectedBytes.Length + 4; // slight extra room
        int retLen = headerSize + payloadLen;
        int capacity = 512;
        IntPtr pBuf = Marshal.AllocHGlobal(capacity);
        try
        {
            IntPtr bufferPtr = IntPtr.Add(pBuf, headerSize);
            Marshal.Copy(expectedBytes, 0, bufferPtr, expectedBytes.Length);

            var ustr = new Win32ProcessInspector.UNICODE_STRING
            {
                Length = (ushort)expectedBytes.Length,
                MaximumLength = (ushort)(expectedBytes.Length + 2),
                Buffer = bufferPtr
            };
            Marshal.StructureToPtr(ustr, pBuf, false);

            var result = Win32ProcessInspector.TryDecodeUnicodeStringFromBuffer(pBuf, retLen, capacity);
            Assert.Equal(expected, result);
        }
        finally
        {
            Marshal.FreeHGlobal(pBuf);
        }
    }

    [Fact]
    public void ResolveClassification_TransientCommandLineFailure_DoesNotPoisonCacheAndRetriesNextPoll()
    {
        var inspector = new Win32ProcessInspector();
        inspector.ClearCache();

        const int pid = 1234;
        const long startTicks = 987654321L;
        const string exe = "node.exe";

        // Poll 1: Command line read fails (transient null)
        var poll1 = inspector.ResolveClassification(
            pid,
            startTicks,
            exe,
            readCommandLine: () => null,
            capturedGen: inspector.CacheGeneration);

        Assert.Null(poll1.Provider); // Exe-only fallback for node
        Assert.False(inspector.TryGetCachedClassification(pid, startTicks, out _));
        Assert.Equal(0, inspector.CacheCount);

        // Poll 2: Command line read succeeds
        var poll2 = inspector.ResolveClassification(
            pid,
            startTicks,
            exe,
            readCommandLine: () => "node.exe C:\\tools\\claude.js",
            capturedGen: inspector.CacheGeneration);

        Assert.Equal("CLAUDE", poll2.Provider);
        Assert.True(inspector.TryGetCachedClassification(pid, startTicks, out var cached));
        Assert.NotNull(cached);
        Assert.Equal("CLAUDE", cached.Provider);
        Assert.Equal(1, inspector.CacheCount);

        // Poll 3: Command line returns null, but cached entry is served
        var poll3 = inspector.ResolveClassification(
            pid,
            startTicks,
            exe,
            readCommandLine: () => null,
            capturedGen: inspector.CacheGeneration);

        Assert.Equal("CLAUDE", poll3.Provider);
    }

    [Fact]
    public void ResolveClassification_NonPositiveStartTicks_NeverCached()
    {
        var inspector = new Win32ProcessInspector();
        inspector.ClearCache();

        const int pid = 4321;
        const long startTicks = 0L; // Non-positive start ticks
        const string exe = "omp.exe";

        var result = inspector.ResolveClassification(
            pid,
            startTicks,
            exe,
            readCommandLine: () => "omp.exe --task",
            capturedGen: inspector.CacheGeneration);

        Assert.Equal("OMP", result.Provider);
        Assert.False(inspector.TryGetCachedClassification(pid, startTicks, out _));
        Assert.Equal(0, inspector.CacheCount);
    }

    [Fact]
    public void ResolveClassification_ClearCacheDuringWork_DoesNotInsertStaleResult()
    {
        var inspector = new Win32ProcessInspector();
        inspector.ClearCache();

        long capturedGen = inspector.CacheGeneration;

        // ClearCache occurs while work is in flight (bumps generation)
        inspector.ClearCache();

        var result = inspector.ResolveClassification(
            pid: 5555,
            startTicks: 111111L,
            exeName: "claude.exe",
            readCommandLine: () => "claude.exe",
            capturedGen: capturedGen);

        Assert.Equal("CLAUDE", result.Provider);
        // Cache must remain empty because capturedGen was older than current generation!
        Assert.Equal(0, inspector.CacheCount);
        Assert.False(inspector.TryGetCachedClassification(5555, 111111L, out _));
    }

    [Fact]
    public void ResolveClassification_NullCommandLine_Python_DoesNotPoisonCacheAndRetriesNextPoll()
    {
        var inspector = new Win32ProcessInspector();
        inspector.ClearCache();

        const int pid = 6789;
        const long startTicks = 123456789L;
        const string exe = "python.exe";

        // Poll 1: Command line read returns null -> returns execute fallback and does NOT cache it
        var poll1 = inspector.ResolveClassification(
            pid,
            startTicks,
            exe,
            readCommandLine: () => null,
            capturedGen: inspector.CacheGeneration);

        Assert.Equal("execute", poll1.RoleHint);
        Assert.False(inspector.TryGetCachedClassification(pid, startTicks, out _));
        Assert.Equal(0, inspector.CacheCount);

        // Poll 2: Same PID & startTicks with "python.exe -m pytest" retries reading command line and returns test
        var poll2 = inspector.ResolveClassification(
            pid,
            startTicks,
            exe,
            readCommandLine: () => "python.exe -m pytest",
            capturedGen: inspector.CacheGeneration);

        Assert.Equal("test", poll2.RoleHint);
        Assert.True(inspector.TryGetCachedClassification(pid, startTicks, out var cached));
        Assert.NotNull(cached);
        Assert.Equal("test", cached.RoleHint);
        Assert.Equal(1, inspector.CacheCount);
    }

    [Theory]
    [InlineData("claude.exe", 10_000L, 9_000L, @"C:\Users\DELL\AppData\Local\claude.exe", true)]
    [InlineData("claude.exe", 10_000L, 10_000L, @"C:\Tools\CLAUDE.EXE", true)]
    [InlineData("claude.exe", 10_000L, 10_001L, @"C:\Users\DELL\AppData\Local\claude.exe", false)] // Created AFTER snapshot
    [InlineData("claude.exe", 10_000L, 0L, @"C:\Users\DELL\AppData\Local\claude.exe", false)]      // Zero creation time
    [InlineData("claude.exe", 10_000L, 9_000L, @"C:\Windows\System32\notepad.exe", false)]         // Mismatched executable basename
    [InlineData("claude.exe", 10_000L, 9_000L, null, false)]                                       // Missing image path
    [InlineData("Orca.exe", 10_000L, 10_500L, @"C:\Program Files\Orca\Orca.exe", false)]           // Ancestry row created AFTER snapshot
    [InlineData("Orca.exe", 10_000L, 8_000L, @"C:\Windows\System32\cmd.exe", false)]               // Ancestry row mismatched basename
    public void VerifyProcessHandleIdentity_ChecksSnapshotWindowAndExecutableBasename(
        string toolhelpExe,
        long snapshotUtcTicks,
        long handleCreationTicks,
        string? handleImagePath,
        bool expectedValid)
    {
        bool actual = Win32ProcessInspector.VerifyProcessHandleIdentity(
            toolhelpExe,
            snapshotUtcTicks,
            handleCreationTicks,
            handleImagePath);

        Assert.Equal(expectedValid, actual);
    }

    [Fact]
    public void InspectFromRawSnapshot_PidReuseBetweenToolhelpAndOpenProcess_RejectsStaleCandidateAndAncestryRows()
    {
        var inspector = new Win32ProcessInspector();
        inspector.ClearCache();

        const long snapshotUtcTicks = 1_000_000L;

        // Toolhelp rows:
        // - PID 500: stale ancestry-only Orca.exe reused by a post-snapshot process
        // - PID 501: stale ancestry-only Orca.exe reused by a mismatched executable (cmd.exe)
        // - PID 1001: stale candidate claude.exe reused by a post-snapshot replacement process
        // - PID 1002: stale candidate omp.exe reused by a mismatched replacement executable (notepad.exe)
        // - PID 2000: genuine live claude.exe whose stale ParentPid happens to be PID 500 (must NOT be falsely linked to rejected Orca PID 500!)
        // - PID 2001: genuine live omp.exe whose stale ParentPid happens to be PID 501 (must NOT be falsely linked to rejected Orca PID 501!)
        var rawEntries = new[]
        {
            new Win32ProcessInspector.RawToolhelpEntry(500, 1, "Orca.exe"),
            new Win32ProcessInspector.RawToolhelpEntry(501, 1, "Orca.exe"),
            new Win32ProcessInspector.RawToolhelpEntry(1001, 10, "claude.exe"),
            new Win32ProcessInspector.RawToolhelpEntry(1002, 10, "omp.exe"),
            new Win32ProcessInspector.RawToolhelpEntry(2000, 500, "claude.exe"),
            new Win32ProcessInspector.RawToolhelpEntry(2001, 501, "omp.exe")
        };

        bool readCmdCalledFor1001 = false;
        bool readCmdCalledFor1002 = false;

        var items = inspector.InspectFromRawSnapshot(
            rawEntries,
            snapshotUtcTicks,
            (pid, _, onHandle) =>
            {
                var data = pid switch
                {
                    // Ancestry-only PID 500: post-snapshot creation time (reused after snapshot!)
                    500 => new Win32ProcessInspector.OpenedProcessHandleData(
                        StartTimeTicks: snapshotUtcTicks + 5000,
                        TotalCpuTimeMs: 0,
                        ImagePath: @"C:\Program Files\Orca\Orca.exe",
                        ReadIoCounters: () => (false, 0, 0),
                        ReadCommandLine: () => null),

                    // Ancestry-only PID 501: mismatched executable basename (reused by cmd.exe!)
                    501 => new Win32ProcessInspector.OpenedProcessHandleData(
                        StartTimeTicks: snapshotUtcTicks - 5000,
                        TotalCpuTimeMs: 0,
                        ImagePath: @"C:\Windows\System32\cmd.exe",
                        ReadIoCounters: () => (false, 0, 0),
                        ReadCommandLine: () => null),

                    // Stale candidate PID 1001: post-snapshot creation time
                    1001 => new Win32ProcessInspector.OpenedProcessHandleData(
                        StartTimeTicks: snapshotUtcTicks + 2500,
                        TotalCpuTimeMs: 900,
                        ImagePath: @"C:\Tools\claude.exe",
                        ReadIoCounters: () => (true, 999_999, 999_999),
                        ReadCommandLine: () => { readCmdCalledFor1001 = true; return "claude.exe --live"; }),

                    // Stale candidate PID 1002: mismatched executable basename (notepad.exe)
                    1002 => new Win32ProcessInspector.OpenedProcessHandleData(
                        StartTimeTicks: snapshotUtcTicks - 2000,
                        TotalCpuTimeMs: 400,
                        ImagePath: @"C:\Windows\System32\notepad.exe",
                        ReadIoCounters: () => (true, 888_888, 888_888),
                        ReadCommandLine: () => { readCmdCalledFor1002 = true; return "notepad.exe notes.txt"; }),

                    // Genuine live candidate PID 2000: pre-snapshot creation time AND matching basename
                    2000 => new Win32ProcessInspector.OpenedProcessHandleData(
                        StartTimeTicks: snapshotUtcTicks - 10_000,
                        TotalCpuTimeMs: 120,
                        ImagePath: @"C:\Tools\claude.exe",
                        ReadIoCounters: () => (true, 1000, 1000),
                        ReadCommandLine: () => "claude.exe"),

                    // Genuine live candidate PID 2001: pre-snapshot creation time AND matching basename
                    2001 => new Win32ProcessInspector.OpenedProcessHandleData(
                        StartTimeTicks: snapshotUtcTicks - 8_000,
                        TotalCpuTimeMs: 80,
                        ImagePath: @"C:\Tools\omp.exe",
                        ReadIoCounters: () => (true, 1000, 1000),
                        ReadCommandLine: () => "omp.exe"),

                    _ => default
                };

                return onHandle(data);
            });

        // 1. Ancestry-only rows (PID 500 post-snapshot, PID 501 mismatched basename) MUST be rejected with StartTimeTicks == 0
        var orca500 = Assert.Single(items, p => p.ProcessId == 500);
        Assert.Equal(0, orca500.StartTimeTicks);

        var orca501 = Assert.Single(items, p => p.ProcessId == 501);
        Assert.Equal(0, orca501.StartTimeTicks);

        // 2. Stale candidate rows (PID 1001 post-snapshot, PID 1002 mismatched basename) MUST be rejected:
        //    StartTimeTicks == 0, Classification.Provider == null, command line never read, never cached!
        var stale1001 = Assert.Single(items, p => p.ProcessId == 1001);
        Assert.Equal(0, stale1001.StartTimeTicks);
        Assert.Equal(0, stale1001.TotalCpuTimeMs);
        Assert.False(stale1001.HasIoCounters);
        Assert.NotNull(stale1001.Classification);
        Assert.Null(stale1001.Classification!.Provider);
        Assert.False(readCmdCalledFor1001);
        Assert.False(inspector.TryGetCachedClassification(1001, snapshotUtcTicks + 2500, out _));

        var stale1002 = Assert.Single(items, p => p.ProcessId == 1002);
        Assert.Equal(0, stale1002.StartTimeTicks);
        Assert.Equal(0, stale1002.TotalCpuTimeMs);
        Assert.False(stale1002.HasIoCounters);
        Assert.NotNull(stale1002.Classification);
        Assert.Null(stale1002.Classification!.Provider);
        Assert.False(readCmdCalledFor1002);
        Assert.False(inspector.TryGetCachedClassification(1002, snapshotUtcTicks - 2000, out _));

        // 3. Genuine live candidates (PID 2000 & 2001) pass handle verification
        var live2000 = Assert.Single(items, p => p.ProcessId == 2000);
        Assert.Equal(snapshotUtcTicks - 10_000, live2000.StartTimeTicks);
        Assert.Equal("CLAUDE", live2000.Classification?.Provider);

        var live2001 = Assert.Single(items, p => p.ProcessId == 2001);
        Assert.Equal(snapshotUtcTicks - 8_000, live2001.StartTimeTicks);
        Assert.Equal("OMP", live2001.Classification?.Provider);

        // 4. Feeding these inspected rows into ProcessAgentCollector:
        //    - Stale PIDs 1001 & 1002 are NEVER published (no stale agent name paired with replacement birth time!)
        //    - Valid PIDs 2000 & 2001 ARE published because rejected Orca ancestry rows (StartTimeTicks == 0) cannot form parent edges!
        var aggregator = new KageDesk.Core.State.HqStateAggregator();
        var collector = new ProcessAgentCollector(
            aggregator,
            enabled: true,
            inspector: new StaticProcessInspector(items));

        var detected = collector.DetectProcesses();
        Assert.Equal(2, detected.Count);
        Assert.DoesNotContain(detected, a => a.ProcessId is 1001 or 1002);
        Assert.DoesNotContain(detected, a => a.ProcessStartTicks == snapshotUtcTicks + 2500);
        Assert.DoesNotContain(detected, a => a.ProcessStartTicks == snapshotUtcTicks - 2000);

        var detectedClaude = Assert.Single(detected, a => a.ProcessId == 2000);
        Assert.Equal(snapshotUtcTicks - 10_000, detectedClaude.ProcessStartTicks);

        var detectedOmp = Assert.Single(detected, a => a.ProcessId == 2001);
        Assert.Equal(snapshotUtcTicks - 8_000, detectedOmp.ProcessStartTicks);
    }

    private sealed class StaticProcessInspector : IProcessInspectionProvider
    {
        private readonly IReadOnlyList<ProcessItem> _items;
        public StaticProcessInspector(IReadOnlyList<ProcessItem> items) => _items = items;
        public IReadOnlyList<ProcessItem> InspectProcesses() => _items;
    }
}
