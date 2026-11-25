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
}
