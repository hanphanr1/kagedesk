using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KageDesk.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace KageDesk.Core.Collectors;

public sealed record SessionWatchRoot(
    string Provider,
    string DisplayName,
    string DirectoryPath,
    string SearchPattern = "*.jsonl"
);

public sealed record DerivedSessionActivity(
    string Provider,
    string DisplayName,
    AgentState State,
    string? DerivedActionToken,
    DateTimeOffset ObservedAt,
    bool IsExplicitIdle = false,
    int? CorrelatedProcessId = null,
    long? CorrelatedProcessStartTicks = null
);

/// <summary>
/// Lightweight, privacy-safe local session/transcript log watcher.
/// Uses a single serialized bounded consumer over FileSystemWatcher events and maintenance ticks
/// with strictly bounded I/O (&lt;= 8 KB total per read including header probe),
/// UTF-8 Span/Utf8JsonReader parsing inside a reusable cleared byte buffer, and extracts ONLY derived
/// non-sensitive activity state and canonical tool action tokens.
/// Only bounded file path/offset/identity bookkeeping (capped at MaxTrackedFiles) is kept in memory;
/// zero prompt text, command arguments, or secrets are materialized as strings or emitted.
/// </summary>
public sealed class SessionLogActivityCollector : IDisposable
{
    public const int MaxTailReadBytes = 8 * 1024;
    private const int MaxTrackedFiles = 64;
    private const int MaxPendingPaths = 256;
    private const int MaxReconcileFilesPerRoot = 32;
    private const int HeaderFingerprintBytes = 64;

    private readonly record struct VerifiedProcessIdentity(int ProcessId, long ProcessStartTicks)
    {
        public bool IsValid => ProcessId > 0 && ProcessStartTicks > 0;
    }

    private readonly record struct ObservedProcessIdentity(
        VerifiedProcessIdentity Identity,
        DateTimeOffset ObservedAt
    );

    private sealed class TrackedFileState
    {
        public string SessionId = string.Empty;
        public long LastCompleteRecordOffset;
        public bool AtRecordBoundary;
        public DateTime CreationTimeUtc;
        public ulong VolumeSerialNumber;
        public ulong FileIndex;
        public byte[]? HeaderFingerprint;
        public int HeaderFingerprintLength;
        public DateTime LastSeenUtc;
        public string Provider = string.Empty;
        public ObservedProcessIdentity? VerifiedIdentity;
        public bool IdentityAmbiguous;
        public bool LatestActivityHasOwnIdentity;
        public DerivedSessionActivity? LatestActivity;
    }

    private sealed class ActiveWatcherEntry
    {
        public required SessionWatchRoot Root { get; init; }
        public required FileSystemWatcher Watcher { get; init; }
        public ulong DirectoryVolumeSerial { get; init; }
        public ulong DirectoryFileIndex { get; init; }
    }

    private readonly IReadOnlyList<SessionWatchRoot> _roots;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _debounceWindow;
    private readonly TimeSpan _maxCoalescingDelay;
    private readonly TimeSpan _workingHoldDuration;
    private readonly TimeSpan _doneHoldDuration;
    private readonly TimeSpan _explicitIdleHoldDuration;
    private readonly TimeSpan _maintenanceInterval;

    private readonly object _stateLock = new();
    private readonly object _readBufferLock = new();
    private readonly byte[] _readBuffer = new byte[MaxTailReadBytes];
    private readonly byte[] _headerProbeBuffer = new byte[HeaderFingerprintBytes];

    private readonly List<ActiveWatcherEntry> _watchers = new();
    private readonly Dictionary<string, TrackedFileState> _fileStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _debounceTimer;
    private Timer? _maintenanceTimer;
    private DateTimeOffset? _firstPendingAt;
    private bool _overflowPending;
    private bool _maintenancePending;
    private bool _immediateTimerArmed;
    private bool _consumerScheduledOrRunning;
    private readonly ManualResetEventSlim _consumerIdleEvent = new(initialState: true);

    private long _stopGeneration;
    private long _removalEpoch;
    private bool _started;
    private bool _disposed;

    internal delegate int LowLevelFileReadDelegate(SafeFileHandle handle, Span<byte> destination, long fileOffset);

    internal Action? OnMaintenanceStepForTest { get; set; }
    internal int LastTotalFileBytesReadForTest { get; private set; }
    internal LowLevelFileReadDelegate? LowLevelReadOverrideForTest { get; set; }

    public event EventHandler<IReadOnlyList<AgentSnapshot>>? ActivityChanged;

    public SessionLogActivityCollector(
        IReadOnlyList<SessionWatchRoot>? watchRoots = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? debounceWindow = null,
        TimeSpan? workingHoldDuration = null,
        TimeSpan? doneHoldDuration = null,
        TimeSpan? staleExpiryDuration = null,
        TimeSpan? maxCoalescingDelay = null,
        TimeSpan? maintenanceInterval = null)
    {
        _roots = watchRoots ?? BuildDefaultWatchRoots();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _debounceWindow = debounceWindow ?? TimeSpan.FromMilliseconds(150);
        _maxCoalescingDelay = maxCoalescingDelay ?? TimeSpan.FromMilliseconds(500);
        if (_maxCoalescingDelay < _debounceWindow)
        {
            _maxCoalescingDelay = _debounceWindow;
        }
        _workingHoldDuration = workingHoldDuration ?? TimeSpan.FromSeconds(20);
        _doneHoldDuration = doneHoldDuration ?? TimeSpan.FromSeconds(10);
        _explicitIdleHoldDuration = staleExpiryDuration ?? TimeSpan.FromSeconds(20);
        _maintenanceInterval = maintenanceInterval ?? TimeSpan.FromSeconds(5);
    }

    public static IReadOnlyList<SessionWatchRoot> BuildDefaultWatchRoots(string? userProfile = null, string? appData = null)
    {
        string home = string.IsNullOrWhiteSpace(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfile;
        string roaming = string.IsNullOrWhiteSpace(appData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : appData;

        string? codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        string codexSessions = !string.IsNullOrWhiteSpace(codexHome)
            ? Path.Combine(codexHome.Trim(), "sessions")
            : Path.Combine(home, ".codex", "sessions");

        return new[]
        {
            new SessionWatchRoot("claude", "Claude", Path.Combine(home, ".claude", "projects"), "*.jsonl"),
            new SessionWatchRoot("codex", "Codex", codexSessions, "*.jsonl"),
            new SessionWatchRoot("omp", "OMP", Path.Combine(home, ".omp", "agent", "sessions"), "*.jsonl"),
            new SessionWatchRoot("omp", "OMP", Path.Combine(home, ".pi", "agent", "sessions"), "*.jsonl"),
            new SessionWatchRoot("omp", "OMP", Path.Combine(roaming, "omp", "sessions"), "*.jsonl")
        };
    }

    public long CurrentGeneration => Volatile.Read(ref _stopGeneration);

    public int ActiveWatcherCount
    {
        get
        {
            lock (_stateLock)
            {
                return _watchers.Count;
            }
        }
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_disposed || _started) return;

            var createdWatchers = new List<ActiveWatcherEntry>();
            try
            {
                foreach (var root in _roots)
                {
                    var entry = TryCreateWatcherLocked(root);
                    if (entry != null)
                    {
                        createdWatchers.Add(entry);
                    }
                }

                _watchers.AddRange(createdWatchers);
                _debounceTimer = new Timer(_ => TriggerConsumerDrain(), null, Timeout.Infinite, Timeout.Infinite);
                _maintenanceTimer = new Timer(_ => RunMaintenanceTick(), null, _maintenanceInterval, _maintenanceInterval);
                _started = true;
            }
            catch
            {
                // Dispose any partially initialized watchers/timers if startup fails midway
                foreach (var w in createdWatchers)
                {
                    DisposeWatcherSafe(w.Watcher);
                }
                _watchers.Clear();
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                _maintenanceTimer?.Dispose();
                _maintenanceTimer = null;
                _started = false;
                throw;
            }
        }
    }

    public void Stop()
    {
        List<ActiveWatcherEntry> watchersToDispose;
        Timer? debounceToDispose;
        Timer? maintenanceToDispose;

        lock (_stateLock)
        {
            Interlocked.Increment(ref _stopGeneration);
            _started = false;

            watchersToDispose = new List<ActiveWatcherEntry>(_watchers);
            _watchers.Clear();

            debounceToDispose = _debounceTimer;
            _debounceTimer = null;
            maintenanceToDispose = _maintenanceTimer;
            _maintenanceTimer = null;

            _pendingPaths.Clear();
            _firstPendingAt = null;
            _overflowPending = false;
            _maintenancePending = false;
            _immediateTimerArmed = false;
        }

        foreach (var entry in watchersToDispose)
        {
            DisposeWatcherSafe(entry.Watcher);
        }

        debounceToDispose?.Dispose();
        maintenanceToDispose?.Dispose();

        // Drain/synchronize any in-flight consumer callback before returning from Stop/Dispose
        try
        {
            _consumerIdleEvent.Wait(TimeSpan.FromSeconds(5));
        }
        catch (ObjectDisposedException) { }

        lock (_stateLock)
        {
            _fileStates.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _consumerIdleEvent.Dispose();
    }

    /// <summary>
    /// Synchronously processes a single file delta using strictly bounded I/O (&lt;= 8 KB total)
    /// into a reusable cleared UTF-8 buffer.
    /// </summary>
    public DerivedSessionActivity? ProcessFileDelta(string filePath, string provider, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        long generation = Volatile.Read(ref _stopGeneration);
        DateTimeOffset now = _clock();
        string resolvedDisplay = !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : FormatProviderDisplay(provider);

        ParsedDeltaBatch parsedBatch = ReadAndParseFileDeltaInPlace(filePath, provider, resolvedDisplay, now);
        if (parsedBatch.Activity == null && !parsedBatch.HasIdentityObservation)
            return null;

        DerivedSessionActivity? derived = parsedBatch.Activity;
        IReadOnlyList<AgentSnapshot>? snapshots = null;
        lock (_stateLock)
        {
            if (generation != Volatile.Read(ref _stopGeneration) || _disposed)
            {
                return null;
            }

            if (_fileStates.TryGetValue(filePath, out var st))
            {
                derived = ApplyTrackedSessionMetadataLocked(st, filePath, provider, parsedBatch);
            }

            if (derived != null)
            {
                snapshots = BuildCurrentSnapshotsLocked(now);
            }
        }

        if (snapshots != null && generation == Volatile.Read(ref _stopGeneration) && !_disposed)
        {
            ActivityChanged?.Invoke(this, snapshots);
        }

        return derived;
    }

    public IReadOnlyList<AgentSnapshot> GetCurrentSnapshots()
    {
        lock (_stateLock)
        {
            return BuildCurrentSnapshotsLocked(_clock());
        }
    }

    /// <summary>
    /// Reads at most <see cref="MaxTailReadBytes"/> total bytes (including the 64-byte header probe)
    /// and returns complete newline-terminated UTF-8 bytes from the file's previous committed record boundary.
    /// </summary>
    public byte[]? ReadBoundedTailDelta(string filePath)
    {
        lock (_readBufferLock)
        {
            try
            {
                int count = ReadCompleteRecordsIntoBufferLocked(filePath, out int sliceStart, out _);
                if (count <= 0) return null;

                byte[] copy = new byte[count];
                Buffer.BlockCopy(_readBuffer, sliceStart, copy, 0, count);
                return copy;
            }
            finally
            {
                Array.Clear(_readBuffer, 0, _readBuffer.Length);
                Array.Clear(_headerProbeBuffer, 0, _headerProbeBuffer.Length);
            }
        }
    }

    /// <summary>
    /// Internal test hook to simulate a FileSystemWatcher buffer overflow and run bounded reconciliation.
    /// </summary>
    internal void SimulateWatcherOverflow()
    {
        lock (_stateLock)
        {
            if (_disposed || !_started) return;
            _overflowPending = true;
            ScheduleConsumerLocked(_clock(), forceImmediate: true);
        }
    }

    /// <summary>
    /// Internal test hook to simulate a FileSystemWatcher.Error (dead/broken watcher) on a watched root.
    /// </summary>
    internal void SimulateWatcherErrorForTest(string directoryPath)
    {
        ActiveWatcherEntry? target = null;
        lock (_stateLock)
        {
            foreach (var w in _watchers)
            {
                if (string.Equals(w.Root.DirectoryPath, directoryPath, StringComparison.OrdinalIgnoreCase))
                {
                    target = w;
                    break;
                }
            }
        }

        if (target != null)
        {
            HandleWatcherError(target);
        }
    }

    /// <summary>
    /// Internal test hook to trigger one unified maintenance tick through the single serial consumer.
    /// </summary>
    internal void TriggerMaintenanceNowForTest()
    {
        RunMaintenanceTick();
    }

    /// <summary>
    /// Internal test hook to trigger bounded missing-root &amp; dead-watcher reconciliation synchronously.
    /// </summary>
    internal void ReconcileMissingRootsNow()
    {
        ReconcileWatchersAndMissingRoots();
    }

    /// <summary>
    /// Internal test hook to simulate file deletion/rename invalidation deterministically.
    /// </summary>
    internal void HandleFileRemoved(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        long generation = Volatile.Read(ref _stopGeneration);
        DateTimeOffset now = _clock();
        IReadOnlyList<AgentSnapshot>? snapshots = null;

        lock (_stateLock)
        {
            if (_disposed || generation != Volatile.Read(ref _stopGeneration)) return;
            Interlocked.Increment(ref _removalEpoch);
            _pendingPaths.Remove(fullPath);

            if (_fileStates.Remove(fullPath))
            {
                snapshots = BuildCurrentSnapshotsLocked(now);
            }
        }

        if (snapshots != null && generation == Volatile.Read(ref _stopGeneration) && !_disposed)
        {
            ActivityChanged?.Invoke(this, snapshots);
        }
    }

    private static string ComputeSessionId(string provider, string filePath)
    {
        string normProvider = string.IsNullOrWhiteSpace(provider) ? "external" : provider.Trim().ToLowerInvariant();
        string canonical = Path.GetFullPath(filePath).ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        string shortHash = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return $"session:{normProvider}:{shortHash}";
    }

    private readonly record struct ParsedLineResult(
        DerivedSessionActivity? Activity,
        VerifiedProcessIdentity? VerifiedIdentity,
        DateTimeOffset? EventTimestamp
    );

    private readonly record struct ParsedDeltaBatch(
        DerivedSessionActivity? Activity,
        bool ActivityHasOwnIdentity,
        ObservedProcessIdentity? UnambiguousIdentity,
        bool HasConflictingIdentities
    )
    {
        public bool HasIdentityObservation =>
            HasConflictingIdentities || (UnambiguousIdentity is { Identity.IsValid: true });
    }

    private static DerivedSessionActivity? ApplyTrackedSessionMetadataLocked(
        TrackedFileState st,
        string filePath,
        string provider,
        ParsedDeltaBatch batch)
    {
        st.Provider = provider;
        if (string.IsNullOrEmpty(st.SessionId))
        {
            st.SessionId = ComputeSessionId(provider, filePath);
        }

        // Accumulate verified (pid, process_start_ticks) tuples with their event timestamps,
        // marking the file's session-level identity ambiguous if competing tuples are observed.
        if (batch.HasConflictingIdentities)
        {
            st.IdentityAmbiguous = true;
            st.VerifiedIdentity = null;
        }
        else if (batch.UnambiguousIdentity is { Identity.IsValid: true } batchId)
        {
            if (!st.IdentityAmbiguous)
            {
                if (st.VerifiedIdentity == null)
                {
                    st.VerifiedIdentity = batchId;
                }
                else if (st.VerifiedIdentity.Value.Identity == batchId.Identity)
                {
                    if (batchId.ObservedAt > st.VerifiedIdentity.Value.ObservedAt)
                    {
                        st.VerifiedIdentity = batchId;
                    }
                }
                else
                {
                    // Multiple conflicting (pid, process_start_ticks) tuples observed across deltas for this file.
                    st.IdentityAmbiguous = true;
                    st.VerifiedIdentity = null;
                }
            }
        }

        DerivedSessionActivity? candidate = batch.Activity;
        if (candidate != null)
        {
            // Precedence:
            // 1. If the selected freshest activity carries its own verified (pid, process_start_ticks), ALWAYS keep it!
            // 2. Otherwise, attach the file's single unambiguous verified tuple, or decline correlation if ambiguous.
            if (!batch.ActivityHasOwnIdentity)
            {
                if (!st.IdentityAmbiguous && st.VerifiedIdentity is { Identity.IsValid: true } unambiguous)
                {
                    candidate = candidate with
                    {
                        CorrelatedProcessId = unambiguous.Identity.ProcessId,
                        CorrelatedProcessStartTicks = unambiguous.Identity.ProcessStartTicks
                    };
                }
                else if (candidate.CorrelatedProcessId.HasValue || candidate.CorrelatedProcessStartTicks.HasValue)
                {
                    candidate = candidate with
                    {
                        CorrelatedProcessId = null,
                        CorrelatedProcessStartTicks = null
                    };
                }
            }

            // Never let a strictly older event timestamp overwrite a fresher tracked activity.
            if (st.LatestActivity == null || candidate.ObservedAt >= st.LatestActivity.ObservedAt)
            {
                st.LatestActivity = candidate;
                st.LatestActivityHasOwnIdentity = batch.ActivityHasOwnIdentity;
            }
        }

        // Reconcile stored activity when identity-only records arrive for an activity that lacked its own tuple.
        if (st.LatestActivity != null && !st.LatestActivityHasOwnIdentity)
        {
            if (st.IdentityAmbiguous)
            {
                if (st.LatestActivity.CorrelatedProcessId.HasValue || st.LatestActivity.CorrelatedProcessStartTicks.HasValue)
                {
                    st.LatestActivity = st.LatestActivity with
                    {
                        CorrelatedProcessId = null,
                        CorrelatedProcessStartTicks = null
                    };
                }
            }
            else if (st.VerifiedIdentity is { Identity.IsValid: true } unambiguous &&
                     (!st.LatestActivity.CorrelatedProcessId.HasValue || !st.LatestActivity.CorrelatedProcessStartTicks.HasValue))
            {
                st.LatestActivity = st.LatestActivity with
                {
                    CorrelatedProcessId = unambiguous.Identity.ProcessId,
                    CorrelatedProcessStartTicks = unambiguous.Identity.ProcessStartTicks
                };
            }
        }

        return st.LatestActivity;
    }

    private ParsedDeltaBatch ReadAndParseFileDeltaInPlace(
        string filePath,
        string provider,
        string displayName,
        DateTimeOffset now)
    {
        lock (_readBufferLock)
        {
            try
            {
                int count = ReadCompleteRecordsIntoBufferLocked(filePath, out int sliceStart, out DateTime fileLastWriteUtc);
                if (count <= 0) return default;

                // Use the file's own last-write timestamp (not batch scan time) as fallback when a JSON line has no explicit timestamp
                DateTimeOffset fallbackTimestamp = fileLastWriteUtc != DateTime.MinValue
                    ? new DateTimeOffset(fileLastWriteUtc, TimeSpan.Zero)
                    : now;

                return ParseBatchFromTail(
                    provider,
                    displayName,
                    new ReadOnlySpan<byte>(_readBuffer, sliceStart, count),
                    fallbackTimestamp);
            }
            finally
            {
                // Always clear raw transcript bytes from reusable buffers immediately after parsing
                Array.Clear(_readBuffer, 0, _readBuffer.Length);
                Array.Clear(_headerProbeBuffer, 0, _headerProbeBuffer.Length);
            }
        }
    }

    private int ReadDirectFromHandle(SafeFileHandle handle, Span<byte> destination, long fileOffset)
    {
        if (destination.IsEmpty)
            return 0;

        var testOverride = LowLevelReadOverrideForTest;
        return testOverride != null
            ? testOverride(handle, destination, fileOffset)
            : RandomAccess.Read(handle, destination, fileOffset);
    }

    private int ReadCompleteRecordsIntoBufferLocked(string filePath, out int sliceStart, out DateTime fileLastWriteUtc)
    {
        sliceStart = 0;
        fileLastWriteUtc = DateTime.MinValue;
        long removalEpochBeforeRead = Volatile.Read(ref _removalEpoch);
        int totalBytesReadFromDisk = 0;
        try
        {
            // Direct unbuffered SafeFileHandle + RandomAccess.Read into reusable cleared buffers:
            // eliminates FileStream's hidden 4 KiB BufferedFileStreamStrategy prefetch completely!
            using SafeFileHandle handle = File.OpenHandle(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);

            long length = RandomAccess.GetLength(handle);
            if (length <= 0)
            {
                LastTotalFileBytesReadForTest = 0;
                return 0;
            }

            FileIdentity identity = ReadFileIdentity(handle, length);
            fileLastWriteUtc = identity.LastWriteTimeUtc;
            int probeLen = identity.HeaderFingerprintLength;
            totalBytesReadFromDisk += probeLen;

            long startOffset;
            bool atRecordBoundary;
            bool wasRotated = false;
            int tailBudgetFromDisk = Math.Max(0, MaxTailReadBytes - totalBytesReadFromDisk);

            lock (_stateLock)
            {
                if (_fileStates.TryGetValue(filePath, out var prev) &&
                    !IsFileRotatedOrReplaced(prev, identity, length))
                {
                    if (prev.LastCompleteRecordOffset == length)
                    {
                        LastTotalFileBytesReadForTest = totalBytesReadFromDisk;
                        return 0;
                    }

                    long delta = length - prev.LastCompleteRecordOffset;
                    long maxDirectDelta = prev.LastCompleteRecordOffset < probeLen
                        ? MaxTailReadBytes
                        : tailBudgetFromDisk;

                    if (delta <= maxDirectDelta)
                    {
                        startOffset = prev.LastCompleteRecordOffset;
                        atRecordBoundary = prev.AtRecordBoundary;
                    }
                    else
                    {
                        startOffset = length - tailBudgetFromDisk;
                        atRecordBoundary = false;
                    }
                }
                else
                {
                    // Brand-new file OR rotated/replaced/truncated file
                    wasRotated = true;
                    if (length <= MaxTailReadBytes)
                    {
                        startOffset = 0;
                        atRecordBoundary = true;
                    }
                    else
                    {
                        // Fold the 64-byte header probe into the 8 KiB total OS read budget
                        startOffset = length - tailBudgetFromDisk;
                        atRecordBoundary = false;
                    }
                }
            }

            // Strictly bound combined underlying OS reads (header probe + tail read) to <= MaxTailReadBytes (8192 B),
            // reusing the already-probed [0..probeLen) header prefix whenever startOffset < probeLen.
            int totalRead = 0;
            int maxChunkBytes;
            if (startOffset < probeLen)
            {
                int reuseCount = (int)Math.Min(probeLen - startOffset, length - startOffset);
                if (reuseCount > 0)
                {
                    Buffer.BlockCopy(_headerProbeBuffer, (int)startOffset, _readBuffer, 0, reuseCount);
                    totalRead = reuseCount;
                }

                long diskStart = startOffset + totalRead;
                int remainingBudget = Math.Max(0, MaxTailReadBytes - totalBytesReadFromDisk);
                int remainingToRead = (int)Math.Min(remainingBudget, Math.Max(0L, length - diskStart));
                maxChunkBytes = totalRead + remainingToRead;

                if (remainingToRead > 0)
                {
                    int diskRead = 0;
                    while (diskRead < remainingToRead)
                    {
                        int read = ReadDirectFromHandle(
                            handle,
                            _readBuffer.AsSpan(totalRead + diskRead, remainingToRead - diskRead),
                            diskStart + diskRead);
                        if (read <= 0) break;
                        diskRead += read;
                    }
                    totalRead += diskRead;
                    totalBytesReadFromDisk += diskRead;
                }
            }
            else
            {
                int remainingBudget = Math.Max(0, MaxTailReadBytes - totalBytesReadFromDisk);
                int toRead = (int)Math.Min(remainingBudget, length - startOffset);
                maxChunkBytes = toRead;
                if (toRead <= 0)
                {
                    LastTotalFileBytesReadForTest = totalBytesReadFromDisk;
                    return 0;
                }

                while (totalRead < toRead)
                {
                    int read = ReadDirectFromHandle(
                        handle,
                        _readBuffer.AsSpan(totalRead, toRead - totalRead),
                        startOffset + totalRead);
                    if (read <= 0) break;
                    totalRead += read;
                }
                totalBytesReadFromDisk += totalRead;
            }

            LastTotalFileBytesReadForTest = totalBytesReadFromDisk;
            if (totalRead <= 0) return 0;

            int parseStart = 0;
            bool boundaryAtParseStart = atRecordBoundary;

            // ONLY skip a leading partial fragment when we jumped mid-file (AtRecordBoundary == false).
            // Never drop the first line when starting at a known record boundary (AtRecordBoundary == true)!
            if (!boundaryAtParseStart)
            {
                int firstLf = Array.IndexOf(_readBuffer, (byte)'\n', 0, totalRead);
                if (firstLf < 0)
                {
                    // No newline anywhere in mid-file chunk
                    long newOffset = totalRead >= maxChunkBytes ? startOffset + totalRead : startOffset;
                    CommitFileState(filePath, identity, newOffset, atRecordBoundary: false, removalEpochBeforeRead, wasRotated);
                    return 0;
                }

                parseStart = firstLf + 1;
                boundaryAtParseStart = true;
            }

            // Parse ONLY complete '\n'-terminated UTF-8 records so an incomplete trailing line or
            // split multi-byte UTF-8 sequence is left uncommitted and cleanly reconstructed on next write.
            int lastLf = -1;
            for (int i = totalRead - 1; i >= parseStart; i--)
            {
                if (_readBuffer[i] == (byte)'\n')
                {
                    lastLf = i;
                    break;
                }
            }

            if (lastLf < parseStart)
            {
                // No complete '\n'-terminated record yet in [parseStart..totalRead)
                if (parseStart > 0)
                {
                    // Advance past skipped leading mid-file fragment to its clean boundary
                    CommitFileState(filePath, identity, startOffset + parseStart, atRecordBoundary: true, removalEpochBeforeRead, wasRotated);
                }
                else if (totalRead >= maxChunkBytes)
                {
                    // Guard against an unbounded >8 KB malformed line without any '\n'
                    CommitFileState(filePath, identity, startOffset + totalRead, atRecordBoundary: false, removalEpochBeforeRead, wasRotated);
                }
                else
                {
                    // Incomplete trailing line / split UTF-8 byte sequence: do NOT advance committed offset!
                    CommitFileState(filePath, identity, startOffset, atRecordBoundary: boundaryAtParseStart, removalEpochBeforeRead, wasRotated);
                }

                return 0;
            }

            int completeLen = (lastLf + 1) - parseStart;
            long committedOffset = startOffset + lastLf + 1;
            if (!CommitFileState(filePath, identity, committedOffset, atRecordBoundary: true, removalEpochBeforeRead, wasRotated))
            {
                return 0;
            }

            sliceStart = parseStart;
            return completeLen;
        }
        catch
        {
            return 0;
        }
    }

    private readonly record struct FileIdentity(
        DateTime CreationTimeUtc,
        DateTime LastWriteTimeUtc,
        ulong VolumeSerialNumber,
        ulong FileIndex,
        byte[] HeaderFingerprint,
        int HeaderFingerprintLength
    );

    private FileIdentity ReadFileIdentity(SafeFileHandle handle, long length)
    {
        DateTime creationUtc = DateTime.MinValue;
        DateTime lastWriteUtc = DateTime.MinValue;
        ulong volumeSerial = 0;
        ulong fileIndex = 0;

        if (OperatingSystem.IsWindows())
        {
            if (GetFileInformationByHandle(handle, out var info))
            {
                long cft = ((long)info.CreationTimeHigh << 32) | info.CreationTimeLow;
                if (cft > 0)
                {
                    creationUtc = DateTime.FromFileTimeUtc(cft);
                }
                long wft = ((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow;
                if (wft > 0)
                {
                    lastWriteUtc = DateTime.FromFileTimeUtc(wft);
                }
                volumeSerial = info.VolumeSerialNumber;
                fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            }
        }

        int probeLen = (int)Math.Min(HeaderFingerprintBytes, length);
        int read = 0;
        while (read < probeLen)
        {
            int n = ReadDirectFromHandle(handle, _headerProbeBuffer.AsSpan(read, probeLen - read), read);
            if (n <= 0) break;
            read += n;
        }

        byte[] hash = SHA256.HashData(new ReadOnlySpan<byte>(_headerProbeBuffer, 0, read));
        return new FileIdentity(creationUtc, lastWriteUtc, volumeSerial, fileIndex, hash, read);
    }

    private static bool IsFileRotatedOrReplaced(TrackedFileState prev, FileIdentity current, long currentLength)
    {
        if (currentLength < prev.LastCompleteRecordOffset)
            return true;

        if (prev.VolumeSerialNumber != 0 && current.VolumeSerialNumber != 0)
        {
            if (prev.VolumeSerialNumber != current.VolumeSerialNumber || prev.FileIndex != current.FileIndex)
                return true;
        }

        if (prev.CreationTimeUtc != DateTime.MinValue && current.CreationTimeUtc != DateTime.MinValue &&
            prev.CreationTimeUtc != current.CreationTimeUtc)
        {
            return true;
        }

        // If header fingerprint length grew from <64 bytes (small initial append), only treat as rotation
        // when the file already had a full 64-byte header that changed, OR the length shrank/changed at same prefix length.
        if (prev.HeaderFingerprint != null && prev.HeaderFingerprintLength == HeaderFingerprintBytes &&
            current.HeaderFingerprintLength == HeaderFingerprintBytes)
        {
            if (!prev.HeaderFingerprint.AsSpan().SequenceEqual(current.HeaderFingerprint))
                return true;
        }
        else if (prev.HeaderFingerprint != null &&
                 prev.HeaderFingerprintLength == current.HeaderFingerprintLength &&
                 !prev.HeaderFingerprint.AsSpan().SequenceEqual(current.HeaderFingerprint))
        {
            return true;
        }

        return false;
    }

    private bool CommitFileState(string filePath, FileIdentity identity, long offset, bool atRecordBoundary, long removalEpochBeforeRead, bool wasRotated)
    {
        lock (_stateLock)
        {
            if (removalEpochBeforeRead != Volatile.Read(ref _removalEpoch) && !File.Exists(filePath))
            {
                return false;
            }

            if (!_fileStates.TryGetValue(filePath, out var st))
            {
                if (_fileStates.Count >= MaxTrackedFiles)
                {
                    EvictOldestTrackedFileLocked();
                }

                st = new TrackedFileState();
                _fileStates[filePath] = st;
            }
            else if (wasRotated)
            {
                // Clear stale identity, ambiguity state, and activity on file rotation/replacement
                st.VerifiedIdentity = null;
                st.IdentityAmbiguous = false;
                st.LatestActivityHasOwnIdentity = false;
                st.LatestActivity = null;
            }

            st.LastCompleteRecordOffset = offset;
            st.AtRecordBoundary = atRecordBoundary;
            st.CreationTimeUtc = identity.CreationTimeUtc;
            st.VolumeSerialNumber = identity.VolumeSerialNumber;
            st.FileIndex = identity.FileIndex;
            st.HeaderFingerprint = identity.HeaderFingerprint;
            st.HeaderFingerprintLength = identity.HeaderFingerprintLength;
            st.LastSeenUtc = _clock().UtcDateTime;
            return true;
        }
    }

    private void EvictOldestTrackedFileLocked()
    {
        string? oldestKey = null;
        DateTime oldestDt = DateTime.MaxValue;
        foreach (var kv in _fileStates)
        {
            if (kv.Value.LastSeenUtc < oldestDt)
            {
                oldestDt = kv.Value.LastSeenUtc;
                oldestKey = kv.Key;
            }
        }
        if (oldestKey != null)
        {
            _fileStates.Remove(oldestKey);
        }
    }

    /// <summary>
    /// Parses only non-sensitive derived state and canonical action token from complete UTF-8 JSONL bytes
    /// using Utf8JsonReader directly over ReadOnlySpan&lt;byte&gt; (zero raw-text string materialization).
    /// Tracks verified (pid, process_start_ticks) tuples with event timestamps across the batch,
    /// preserves the freshest activity's own tuple when present, and declines correlation on competing tuples.
    /// </summary>
    public static DerivedSessionActivity? ParseDerivedActivityFromTail(
        string provider,
        string displayName,
        ReadOnlySpan<byte> tailBytes,
        DateTimeOffset observedAt)
    {
        return ParseBatchFromTail(provider, displayName, tailBytes, observedAt).Activity;
    }

    private static ParsedDeltaBatch ParseBatchFromTail(
        string provider,
        string displayName,
        ReadOnlySpan<byte> tailBytes,
        DateTimeOffset observedAt)
    {
        if (tailBytes.IsEmpty)
            return default;

        DerivedSessionActivity? freshest = null;
        ObservedProcessIdentity? unambiguousBatchIdentity = null;
        bool hasConflictingIdentities = false;
        int pos = 0;

        while (pos < tailBytes.Length)
        {
            int relLf = tailBytes.Slice(pos).IndexOf((byte)'\n');
            ReadOnlySpan<byte> line;
            if (relLf < 0)
            {
                line = tailBytes.Slice(pos);
                pos = tailBytes.Length;
            }
            else
            {
                line = tailBytes.Slice(pos, relLf);
                pos += relLf + 1;
            }

            line = TrimAsciiWhitespace(line);
            if (line.Length < 2 || line[0] != (byte)'{' || line[^1] != (byte)'}')
                continue;

            ParsedLineResult parsed = TryParseSingleJsonLineUtf8(provider, displayName, line, observedAt);

            // Track observed verified (pid, process_start_ticks) tuples together with event timestamp.
            // If multiple distinct tuples appear in the same batch, mark batch identity ambiguous.
            if (parsed.VerifiedIdentity is { IsValid: true } lineId)
            {
                DateTimeOffset idTime = parsed.EventTimestamp ?? observedAt;
                if (!hasConflictingIdentities)
                {
                    if (unambiguousBatchIdentity == null)
                    {
                        unambiguousBatchIdentity = new ObservedProcessIdentity(lineId, idTime);
                    }
                    else if (unambiguousBatchIdentity.Value.Identity == lineId)
                    {
                        if (idTime > unambiguousBatchIdentity.Value.ObservedAt)
                        {
                            unambiguousBatchIdentity = new ObservedProcessIdentity(lineId, idTime);
                        }
                    }
                    else
                    {
                        hasConflictingIdentities = true;
                        unambiguousBatchIdentity = null;
                    }
                }
            }

            if (parsed.Activity != null)
            {
                // Select freshest applicable activity by event timestamp; ties keep later physical order.
                if (freshest == null || parsed.Activity.ObservedAt >= freshest.ObservedAt)
                {
                    freshest = parsed.Activity;
                }
            }
        }

        // Resolution precedence:
        // 1. If the selected freshest activity carries its own verified (pid, process_start_ticks), ALWAYS keep it!
        // 2. Otherwise, attach the single unambiguous verified tuple if present; decline correlation if conflicting.
        bool freshestHasOwnIdentity = freshest != null &&
            freshest.CorrelatedProcessId.HasValue && freshest.CorrelatedProcessId.Value > 0 &&
            freshest.CorrelatedProcessStartTicks.HasValue && freshest.CorrelatedProcessStartTicks.Value > 0;

        if (freshest != null && !freshestHasOwnIdentity &&
            !hasConflictingIdentities &&
            unambiguousBatchIdentity is { Identity.IsValid: true } unambiguous)
        {
            freshest = freshest with
            {
                CorrelatedProcessId = unambiguous.Identity.ProcessId,
                CorrelatedProcessStartTicks = unambiguous.Identity.ProcessStartTicks
            };
        }

        return new ParsedDeltaBatch(
            freshest,
            freshestHasOwnIdentity,
            unambiguousBatchIdentity,
            hasConflictingIdentities);
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> span)
    {
        int start = 0;
        int end = span.Length - 1;
        while (start <= end && IsAsciiWs(span[start])) start++;
        while (end >= start && IsAsciiWs(span[end])) end--;
        return start > end ? ReadOnlySpan<byte>.Empty : span.Slice(start, end - start + 1);
    }

    private static bool IsAsciiWs(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private enum LineSignalKind
    {
        None,
        Working,
        Approval,
        Completion,
        ExplicitIdle
    }

    private static ParsedLineResult TryParseSingleJsonLineUtf8(
        string provider,
        string displayName,
        ReadOnlySpan<byte> utf8Line,
        DateTimeOffset observedAt)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Line, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return default;

            bool isApproval = false;
            bool isCompletion = false;
            bool isExplicitIdle = false;
            bool isWorkingSignal = false;
            string? canonicalToolToken = null;
            int? parsedPid = null;
            long? parsedStartTicks = null;
            DateTimeOffset? parsedTimestamp = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                if (reader.ValueTextEquals("type"u8) || reader.ValueTextEquals("event"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    {
                        ClassifyTypeSpan(ref reader, ref isApproval, ref isCompletion, ref isExplicitIdle, ref isWorkingSignal);
                    }
                    else
                    {
                        SkipIfStructured(ref reader);
                    }
                }
                else if (reader.ValueTextEquals("role"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    {
                        if (EqualsIgnoreCaseAscii(ref reader, "user"u8) || EqualsIgnoreCaseAscii(ref reader, "assistant"u8))
                        {
                            isWorkingSignal = true;
                        }
                    }
                    else
                    {
                        SkipIfStructured(ref reader);
                    }
                }
                else if (reader.ValueTextEquals("stop_reason"u8) || reader.ValueTextEquals("stopReason"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    {
                        ClassifyStopReasonSpan(ref reader, ref isApproval, ref isCompletion);
                    }
                    else
                    {
                        SkipIfStructured(ref reader);
                    }
                }
                else if (reader.ValueTextEquals("tool_name"u8) || reader.ValueTextEquals("toolName"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    {
                        ClassifyToolNameSpan(ref reader, ref isApproval, ref canonicalToolToken);
                    }
                    else
                    {
                        SkipIfStructured(ref reader);
                    }
                }
                else if (reader.ValueTextEquals("timestamp"u8) ||
                         reader.ValueTextEquals("created_at"u8) ||
                         reader.ValueTextEquals("createdAt"u8) ||
                         reader.ValueTextEquals("time"u8) ||
                         reader.ValueTextEquals("ts"u8))
                {
                    if (reader.Read())
                    {
                        if (TryReadEventTimestamp(ref reader, out DateTimeOffset ts))
                        {
                            parsedTimestamp = ts;
                        }
                        else
                        {
                            SkipIfStructured(ref reader);
                        }
                    }
                }
                else if (reader.ValueTextEquals("pid"u8) || reader.ValueTextEquals("process_id"u8) || reader.ValueTextEquals("processId"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int pid) && pid > 0)
                    {
                        parsedPid = pid;
                    }
                    else
                    {
                        SkipIfStructured(ref reader);
                    }
                }
                else if (reader.ValueTextEquals("process_start_ticks"u8) ||
                         reader.ValueTextEquals("processStartTicks"u8) ||
                         reader.ValueTextEquals("start_time_ticks"u8) ||
                         reader.ValueTextEquals("startTimeTicks"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long ticks) && ticks > 0)
                    {
                        parsedStartTicks = ticks;
                    }
                    else
                    {
                        SkipIfStructured(ref reader);
                    }
                }
                else if (reader.ValueTextEquals("message"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                    {
                        ParseMessageObjectUtf8(ref reader, ref isApproval, ref isCompletion, ref isWorkingSignal, ref canonicalToolToken);
                    }
                    else
                    {
                        SkipIfStructured(ref reader);
                    }
                }
                else if (reader.ValueTextEquals("payload"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                    {
                        ParsePayloadObjectUtf8(ref reader, ref isApproval, ref isCompletion, ref isExplicitIdle, ref isWorkingSignal, ref canonicalToolToken, ref parsedPid, ref parsedStartTicks);
                    }
                    else
                    {
                        SkipIfStructured(ref reader);
                    }
                }
                else
                {
                    // Skip all prompt text, inputs, paths, secrets without ever reading or allocating strings
                    reader.Skip();
                }
            }

            // NEVER emit a bare JSON pid without verified start generation ticks!
            VerifiedProcessIdentity? verifiedIdentity = (parsedPid.HasValue && parsedStartTicks.HasValue)
                ? new VerifiedProcessIdentity(parsedPid.Value, parsedStartTicks.Value)
                : null;
            int? verifiedPid = verifiedIdentity?.ProcessId;
            long? verifiedStartTicks = verifiedIdentity?.ProcessStartTicks;
            DateTimeOffset eventTime = parsedTimestamp ?? observedAt;

            DerivedSessionActivity? activity = null;
            if (isApproval)
            {
                activity = new DerivedSessionActivity(provider, displayName, AgentState.NeedsApproval, "approve", eventTime, IsExplicitIdle: false, CorrelatedProcessId: verifiedPid, CorrelatedProcessStartTicks: verifiedStartTicks);
            }
            else if (canonicalToolToken != null)
            {
                activity = new DerivedSessionActivity(provider, displayName, AgentState.Working, canonicalToolToken, eventTime, IsExplicitIdle: false, CorrelatedProcessId: verifiedPid, CorrelatedProcessStartTicks: verifiedStartTicks);
            }
            else if (isCompletion)
            {
                activity = new DerivedSessionActivity(provider, displayName, AgentState.Done, null, eventTime, IsExplicitIdle: false, CorrelatedProcessId: verifiedPid, CorrelatedProcessStartTicks: verifiedStartTicks);
            }
            else if (isExplicitIdle)
            {
                activity = new DerivedSessionActivity(provider, displayName, AgentState.Idle, null, eventTime, IsExplicitIdle: true, CorrelatedProcessId: verifiedPid, CorrelatedProcessStartTicks: verifiedStartTicks);
            }
            else if (isWorkingSignal)
            {
                activity = new DerivedSessionActivity(provider, displayName, AgentState.Working, "think", eventTime, IsExplicitIdle: false, CorrelatedProcessId: verifiedPid, CorrelatedProcessStartTicks: verifiedStartTicks);
            }

            return new ParsedLineResult(activity, verifiedIdentity, verifiedIdentity.HasValue ? eventTime : null);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool TryReadEventTimestamp(ref Utf8JsonReader reader, out DateTimeOffset result)
    {
        result = default;
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.TryGetDateTimeOffset(out result);
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long unix))
        {
            try
            {
                if (unix >= 100_000_000_000L && unix <= 9_999_999_999_999L)
                {
                    result = DateTimeOffset.FromUnixTimeMilliseconds(unix);
                    return true;
                }
                if (unix >= 1_000_000_000L && unix < 100_000_000_000L)
                {
                    result = DateTimeOffset.FromUnixTimeSeconds(unix);
                    return true;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return false;
    }

    private static void ParseMessageObjectUtf8(
        ref Utf8JsonReader reader,
        ref bool isApproval,
        ref bool isCompletion,
        ref bool isWorkingSignal,
        ref string? canonicalToolToken)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("role"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    if (EqualsIgnoreCaseAscii(ref reader, "user"u8) || EqualsIgnoreCaseAscii(ref reader, "assistant"u8))
                    {
                        isWorkingSignal = true;
                    }
                }
                else
                {
                    SkipIfStructured(ref reader);
                }
            }
            else if (reader.ValueTextEquals("stop_reason"u8) || reader.ValueTextEquals("stopReason"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    ClassifyStopReasonSpan(ref reader, ref isApproval, ref isCompletion);
                }
                else
                {
                    SkipIfStructured(ref reader);
                }
            }
            else if (reader.ValueTextEquals("content"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                {
                    ParseMessageContentArrayUtf8(ref reader, ref isApproval, ref canonicalToolToken);
                }
                else
                {
                    SkipIfStructured(ref reader);
                }
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private static void ParseMessageContentArrayUtf8(
        ref Utf8JsonReader reader,
        ref bool isApproval,
        ref string? canonicalToolToken)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                SkipIfStructured(ref reader);
                continue;
            }

            bool isToolBlock = false;
            bool isThinkingBlock = false;
            bool blockApproval = false;
            string? blockToken = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                if (reader.ValueTextEquals("type"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    {
                        if (EqualsIgnoreCaseAscii(ref reader, "tool_use"u8) ||
                            EqualsIgnoreCaseAscii(ref reader, "tool_call"u8) ||
                            EqualsIgnoreCaseAscii(ref reader, "function_call"u8))
                        {
                            isToolBlock = true;
                        }
                        else if (EqualsIgnoreCaseAscii(ref reader, "thinking"u8))
                        {
                            isThinkingBlock = true;
                        }
                    }
                    else
                    {
                        SkipIfStructured(ref reader);
                    }
                }
                else if (reader.ValueTextEquals("name"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    {
                        ClassifyToolNameSpan(ref reader, ref blockApproval, ref blockToken);
                    }
                    else
                    {
                        SkipIfStructured(ref reader);
                    }
                }
                else
                {
                    // Never read input/text/thinking bodies
                    reader.Skip();
                }
            }

            if (isToolBlock)
            {
                if (blockApproval) isApproval = true;
                if (blockToken != null) canonicalToolToken = blockToken;
            }
            else if (isThinkingBlock && canonicalToolToken == null)
            {
                canonicalToolToken = "think";
            }
        }
    }

    private static void ParsePayloadObjectUtf8(
        ref Utf8JsonReader reader,
        ref bool isApproval,
        ref bool isCompletion,
        ref bool isExplicitIdle,
        ref bool isWorkingSignal,
        ref string? canonicalToolToken,
        ref int? parsedPid,
        ref long? parsedStartTicks)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("type"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    ClassifyTypeSpan(ref reader, ref isApproval, ref isCompletion, ref isExplicitIdle, ref isWorkingSignal);
                }
                else
                {
                    SkipIfStructured(ref reader);
                }
            }
            else if (reader.ValueTextEquals("role"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    if (EqualsIgnoreCaseAscii(ref reader, "user"u8) || EqualsIgnoreCaseAscii(ref reader, "assistant"u8))
                    {
                        isWorkingSignal = true;
                    }
                }
                else
                {
                    SkipIfStructured(ref reader);
                }
            }
            else if (reader.ValueTextEquals("name"u8) || reader.ValueTextEquals("tool"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    ClassifyToolNameSpan(ref reader, ref isApproval, ref canonicalToolToken);
                }
                else
                {
                    SkipIfStructured(ref reader);
                }
            }
            else if (reader.ValueTextEquals("pid"u8) || reader.ValueTextEquals("process_id"u8) || reader.ValueTextEquals("processId"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int pid) && pid > 0)
                {
                    parsedPid = pid;
                }
                else
                {
                    SkipIfStructured(ref reader);
                }
            }
            else if (reader.ValueTextEquals("process_start_ticks"u8) ||
                     reader.ValueTextEquals("processStartTicks"u8) ||
                     reader.ValueTextEquals("start_time_ticks"u8) ||
                     reader.ValueTextEquals("startTimeTicks"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long ticks) && ticks > 0)
                {
                    parsedStartTicks = ticks;
                }
                else
                {
                    SkipIfStructured(ref reader);
                }
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private static void SkipIfStructured(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }

    private static void ClassifyTypeSpan(
        ref Utf8JsonReader reader,
        ref bool isApproval,
        ref bool isCompletion,
        ref bool isExplicitIdle,
        ref bool isWorkingSignal)
    {
        if (EqualsIgnoreCaseAscii(ref reader, "permission_request"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "approval_required"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "waiting_for_input"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "needs_approval"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "input_required"u8))
        {
            isApproval = true;
            return;
        }

        if (EqualsIgnoreCaseAscii(ref reader, "turn_complete"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "task_complete"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "response.completed"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "done"u8))
        {
            isCompletion = true;
            return;
        }

        if (EqualsIgnoreCaseAscii(ref reader, "session_idle"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "idle"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "session_end"u8))
        {
            isExplicitIdle = true;
            return;
        }

        if (EqualsIgnoreCaseAscii(ref reader, "user"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "assistant"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "thinking"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "tool_result"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "function_call_output"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "agent_reasoning"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "response_item"u8))
        {
            isWorkingSignal = true;
        }
    }

    private static void ClassifyStopReasonSpan(
        ref Utf8JsonReader reader,
        ref bool isApproval,
        ref bool isCompletion)
    {
        if (EqualsIgnoreCaseAscii(ref reader, "waiting_for_input"u8))
        {
            isApproval = true;
            return;
        }

        if (EqualsIgnoreCaseAscii(ref reader, "end_turn"u8) ||
            EqualsIgnoreCaseAscii(ref reader, "stop"u8))
        {
            isCompletion = true;
        }
    }

    private static void ClassifyToolNameSpan(
        ref Utf8JsonReader reader,
        ref bool isApproval,
        ref string? canonicalToolToken)
    {
        // Decode up to 128 bytes onto stack (never allocating a managed string for raw tool name)
        Span<byte> utf8Buf = stackalloc byte[128];
        int len = CopyUnescapedUtf8(ref reader, utf8Buf);
        if (len <= 0) return;

        ReadOnlySpan<byte> nameUtf8 = utf8Buf.Slice(0, len);
        if (ContainsIgnoreCaseAscii(nameUtf8, "ask_user"u8) ||
            ContainsIgnoreCaseAscii(nameUtf8, "ask_question"u8) ||
            ContainsIgnoreCaseAscii(nameUtf8, "askuserquestion"u8) ||
            ContainsIgnoreCaseAscii(nameUtf8, "request_approval"u8))
        {
            isApproval = true;
            return;
        }

        canonicalToolToken = MapToolNameUtf8ToCanonicalToken(nameUtf8);
    }

    private static int CopyUnescapedUtf8(ref Utf8JsonReader reader, scoped Span<byte> destination)
    {
        if (!reader.ValueIsEscaped && !reader.HasValueSequence)
        {
            ReadOnlySpan<byte> src = reader.ValueSpan;
            int n = Math.Min(src.Length, destination.Length);
            src.Slice(0, n).CopyTo(destination);
            return n;
        }

        try
        {
            return reader.CopyString(destination);
        }
        catch (ArgumentException)
        {
            // Longer than 128 bytes: copy prefix from raw ValueSpan cleanly
            ReadOnlySpan<byte> raw = reader.HasValueSequence ? reader.ValueSequence.FirstSpan : reader.ValueSpan;
            int n = Math.Min(raw.Length, destination.Length);
            raw.Slice(0, n).CopyTo(destination);
            return n;
        }
    }

    private static bool EqualsIgnoreCaseAscii(ref Utf8JsonReader reader, ReadOnlySpan<byte> expectedLowerAscii)
    {
        if (reader.HasValueSequence || reader.ValueIsEscaped)
        {
            Span<byte> buf = stackalloc byte[64];
            if (expectedLowerAscii.Length > buf.Length) return false;
            int n = CopyUnescapedUtf8(ref reader, buf);
            return EqualsIgnoreCaseAsciiSpan(buf.Slice(0, n), expectedLowerAscii);
        }

        return EqualsIgnoreCaseAsciiSpan(reader.ValueSpan, expectedLowerAscii);
    }

    private static bool EqualsIgnoreCaseAsciiSpan(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expectedLowerAscii)
    {
        if (actual.Length != expectedLowerAscii.Length) return false;
        for (int i = 0; i < actual.Length; i++)
        {
            byte a = actual[i];
            if (a >= (byte)'A' && a <= (byte)'Z') a = (byte)(a + 32);
            if (a != expectedLowerAscii[i]) return false;
        }
        return true;
    }

    private static bool ContainsIgnoreCaseAscii(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needleLowerAscii)
    {
        if (needleLowerAscii.IsEmpty) return true;
        if (haystack.Length < needleLowerAscii.Length) return false;

        int limit = haystack.Length - needleLowerAscii.Length;
        for (int i = 0; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < needleLowerAscii.Length; j++)
            {
                byte a = haystack[i + j];
                if (a >= (byte)'A' && a <= (byte)'Z') a = (byte)(a + 32);
                if (a != needleLowerAscii[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }

    private static string MapToolNameUtf8ToCanonicalToken(ReadOnlySpan<byte> rawUtf8)
    {
        if (ContainsIgnoreCaseAscii(rawUtf8, "test"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "pytest"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "jest"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "vitest"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "xunit"u8))
            return "test";

        if (ContainsIgnoreCaseAscii(rawUtf8, "git"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "commit"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "push"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "pull"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "branch"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "merge"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "diff"u8))
            return "git";

        if (ContainsIgnoreCaseAscii(rawUtf8, "edit"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "write"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "patch"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "replace"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "code"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "create"u8))
            return "code";

        if (ContainsIgnoreCaseAscii(rawUtf8, "grep"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "search"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "find"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "glob"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "scan"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "web"u8))
            return "scan";

        if (ContainsIgnoreCaseAscii(rawUtf8, "read"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "view"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "cat"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "inspect"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "review"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "list"u8))
            return "read";

        if (ContainsIgnoreCaseAscii(rawUtf8, "bash"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "shell"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "exec"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "run"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "cmd"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "powershell"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "terminal"u8) ||
            ContainsIgnoreCaseAscii(rawUtf8, "build"u8))
            return "exec";

        return "think";
    }

    /// <summary>
    /// Maps arbitrary tool names strictly to one of the fixed non-sensitive canonical action tokens
    /// so raw tool paths, MCP server identifiers, or secrets never enter KageDesk state.
    /// </summary>
    public static string MapToolNameToCanonicalToken(string rawToolName)
    {
        var (kind, _) = AgentActionClassifier.Classify(AgentState.Working, rawToolName);
        return kind switch
        {
            AgentActionKind.Reading => "read",
            AgentActionKind.Coding => "code",
            AgentActionKind.Executing => "exec",
            AgentActionKind.Testing => "test",
            AgentActionKind.Building => "exec",
            AgentActionKind.GitOps => "git",
            AgentActionKind.Searching => "scan",
            AgentActionKind.Reviewing => "read",
            _ => "think"
        };
    }

    private ActiveWatcherEntry? TryCreateWatcherLocked(SessionWatchRoot root)
    {
        if (string.IsNullOrWhiteSpace(root.DirectoryPath) || !Directory.Exists(root.DirectoryPath))
            return null;

        FileSystemWatcher? watcher = null;
        try
        {
            TryGetDirectoryIdentity(root.DirectoryPath, out ulong dirVol, out ulong dirIdx);

            watcher = new FileSystemWatcher(root.DirectoryPath, root.SearchPattern)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                InternalBufferSize = 16384
            };

            var entry = new ActiveWatcherEntry
            {
                Root = root,
                Watcher = watcher,
                DirectoryVolumeSerial = dirVol,
                DirectoryFileIndex = dirIdx
            };

            string provider = root.Provider;
            void OnChangedOrCreated(object _, FileSystemEventArgs e)
            {
                if (_disposed || string.IsNullOrWhiteSpace(e.FullPath)) return;
                EnqueuePendingFile(e.FullPath, provider);
            }

            watcher.Changed += OnChangedOrCreated;
            watcher.Created += OnChangedOrCreated;
            watcher.Deleted += (_, e) =>
            {
                if (_disposed || string.IsNullOrWhiteSpace(e.FullPath)) return;
                HandleFileRemoved(e.FullPath);
            };
            watcher.Renamed += (_, e) =>
            {
                if (_disposed) return;
                if (!string.IsNullOrWhiteSpace(e.OldFullPath))
                {
                    HandleFileRemoved(e.OldFullPath);
                }
                if (!string.IsNullOrWhiteSpace(e.FullPath))
                {
                    EnqueuePendingFile(e.FullPath, provider);
                }
            };
            watcher.Error += (_, _) => HandleWatcherError(entry);
            watcher.EnableRaisingEvents = true;
            return entry;
        }
        catch
        {
            DisposeWatcherSafe(watcher);
            return null;
        }
    }

    private void HandleWatcherError(ActiveWatcherEntry entry)
    {
        if (_disposed) return;

        FileSystemWatcher? deadWatcher = null;
        lock (_stateLock)
        {
            if (_disposed || !_started) return;

            int idx = _watchers.IndexOf(entry);
            if (idx >= 0)
            {
                deadWatcher = _watchers[idx].Watcher;
                _watchers.RemoveAt(idx);
            }

            _overflowPending = true;
            _maintenancePending = true;
            ScheduleConsumerLocked(_clock(), forceImmediate: false);
        }

        DisposeWatcherSafe(deadWatcher);
    }

    private static bool IsWatcherHealthy(FileSystemWatcher watcher)
    {
        try
        {
            return watcher.EnableRaisingEvents;
        }
        catch
        {
            return false;
        }
    }

    private static void DisposeWatcherSafe(FileSystemWatcher? watcher)
    {
        if (watcher == null) return;
        try { watcher.EnableRaisingEvents = false; } catch { }
        try { watcher.Dispose(); } catch { }
    }

    private void ReconcileWatchersAndMissingRoots()
    {
        List<FileSystemWatcher>? deadWatchers = null;

        lock (_stateLock)
        {
            if (_disposed || !_started) return;

            // 1. Prune dead, broken, deleted, or replaced-directory watchers from active map
            for (int i = _watchers.Count - 1; i >= 0; i--)
            {
                var w = _watchers[i];
                bool rootExists = !string.IsNullOrWhiteSpace(w.Root.DirectoryPath) && Directory.Exists(w.Root.DirectoryPath);
                bool healthy = rootExists && IsWatcherHealthy(w.Watcher);

                if (healthy && w.DirectoryFileIndex != 0 &&
                    TryGetDirectoryIdentity(w.Root.DirectoryPath, out ulong curVol, out ulong curIdx))
                {
                    if (curVol != w.DirectoryVolumeSerial || curIdx != w.DirectoryFileIndex)
                    {
                        healthy = false;
                    }
                }

                if (!healthy)
                {
                    deadWatchers ??= new List<FileSystemWatcher>();
                    deadWatchers.Add(w.Watcher);
                    _watchers.RemoveAt(i);
                }
            }
        }

        // Dispose dead watchers outside lock before recreating fresh ones
        if (deadWatchers != null)
        {
            foreach (var dw in deadWatchers)
            {
                DisposeWatcherSafe(dw);
            }
        }

        // 2. Attach watchers for any existing roots not currently watched
        lock (_stateLock)
        {
            if (_disposed || !_started) return;

            foreach (var root in _roots)
            {
                if (string.IsNullOrWhiteSpace(root.DirectoryPath) || !Directory.Exists(root.DirectoryPath))
                    continue;

                bool alreadyWatched = _watchers.Any(w =>
                    string.Equals(w.Root.DirectoryPath, root.DirectoryPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(w.Root.Provider, root.Provider, StringComparison.OrdinalIgnoreCase));

                if (alreadyWatched) continue;

                var entry = TryCreateWatcherLocked(root);
                if (entry != null)
                {
                    _watchers.Add(entry);
                }
            }
        }
    }

    private void EnqueuePendingFile(string fullPath, string provider)
    {
        lock (_stateLock)
        {
            if (_disposed || !_started) return;

            if (_pendingPaths.Count >= MaxPendingPaths && !_pendingPaths.ContainsKey(fullPath))
            {
                _overflowPending = true;
            }
            else
            {
                _pendingPaths[fullPath] = provider;
            }

            ScheduleConsumerLocked(_clock(), forceImmediate: false);
        }
    }

    internal void EnqueuePathForTest(string fullPath, string provider = "claude") => EnqueuePendingFile(fullPath, provider);

    private void ScheduleConsumerLocked(DateTimeOffset now, bool forceImmediate)
    {
        if (_disposed || !_started || _debounceTimer == null) return;

        // If an immediate drain is already armed, never reset the timer queue node under rapid FSW traffic!
        if (_immediateTimerArmed)
        {
            return;
        }

        _firstPendingAt ??= now;

        TimeSpan due;
        if (forceImmediate)
        {
            due = TimeSpan.Zero;
            _immediateTimerArmed = true;
        }
        else
        {
            // Enforce maximum coalescing delay so continuous rapid writes NEVER starve processing
            TimeSpan elapsedSinceFirst = now - _firstPendingAt.Value;
            TimeSpan remainingMax = _maxCoalescingDelay - elapsedSinceFirst;
            if (remainingMax <= TimeSpan.Zero)
            {
                due = TimeSpan.Zero;
                _immediateTimerArmed = true;
            }
            else
            {
                due = remainingMax < _debounceWindow ? remainingMax : _debounceWindow;
            }
        }

        try
        {
            _debounceTimer.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) { }
    }

    private void TriggerConsumerDrain()
    {
        long generation;
        lock (_stateLock)
        {
            _immediateTimerArmed = false;
            if (_disposed || !_started || _consumerScheduledOrRunning)
                return;

            _consumerScheduledOrRunning = true;
            _consumerIdleEvent.Reset();
            generation = Volatile.Read(ref _stopGeneration);
        }

        ExecuteConsumerWithReschedule(generation);
    }

    private void RunMaintenanceTick()
    {
        long generation;
        lock (_stateLock)
        {
            if (_disposed || !_started) return;
            _maintenancePending = true;

            if (_consumerScheduledOrRunning)
            {
                // Active serial consumer will pick up _maintenancePending before exiting!
                return;
            }

            _consumerScheduledOrRunning = true;
            _consumerIdleEvent.Reset();
            generation = Volatile.Read(ref _stopGeneration);
        }

        ExecuteConsumerWithReschedule(generation);
    }

    /// <summary>
    /// Single unified serial consumer entry point for BOTH pending-path drains AND maintenance.
    /// On EVERY exit, re-checks whether pending paths, overflow, or maintenance were queued while running
    /// and re-arms immediately so a final turn-end append during maintenance can NEVER be swallowed.
    /// </summary>
    private void ExecuteConsumerWithReschedule(long generation)
    {
        try
        {
            RunSerializedConsumerLoop(generation);
        }
        finally
        {
            lock (_stateLock)
            {
                _consumerScheduledOrRunning = false;
                if (!_disposed && _started && (_pendingPaths.Count > 0 || _overflowPending || _maintenancePending))
                {
                    ScheduleConsumerLocked(_clock(), forceImmediate: _maintenancePending || _overflowPending);
                }
                _consumerIdleEvent.Set();
            }
        }
    }

    private void RunSerializedConsumerLoop(long generation)
    {
        while (!_disposed && generation == Volatile.Read(ref _stopGeneration))
        {
            List<(string Path, string Provider)> batch;
            bool runOverflowReconcile;
            bool runMaintenance;

            lock (_stateLock)
            {
                if (!_started || generation != Volatile.Read(ref _stopGeneration))
                    return;

                runMaintenance = _maintenancePending;
                _maintenancePending = false;
                runOverflowReconcile = _overflowPending;
                _overflowPending = false;
                _firstPendingAt = null;
                _immediateTimerArmed = false;

                if (_pendingPaths.Count == 0 && !runOverflowReconcile && !runMaintenance)
                    return;

                batch = new List<(string, string)>(_pendingPaths.Count);
                foreach (var kv in _pendingPaths)
                {
                    batch.Add((kv.Key, kv.Value));
                }
                _pendingPaths.Clear();
            }

            if (runMaintenance)
            {
                ReconcileWatchersAndMissingRoots();
                OnMaintenanceStepForTest?.Invoke();
                PruneAndPublishIfChanged(generation);
            }

            if (runOverflowReconcile)
            {
                if (!runMaintenance)
                {
                    ReconcileWatchersAndMissingRoots();
                }
                AppendOverflowReconciliationFiles(batch);
            }

            if (batch.Count == 0) continue;

            DateTimeOffset now = _clock();
            bool anyUpdated = false;

            foreach (var (path, provider) in batch)
            {
                if (_disposed || generation != Volatile.Read(ref _stopGeneration))
                    return;

                if (!File.Exists(path))
                {
                    lock (_stateLock)
                    {
                        if (_fileStates.Remove(path))
                        {
                            anyUpdated = true;
                        }
                    }
                    continue;
                }

                ParsedDeltaBatch parsedBatch = ReadAndParseFileDeltaInPlace(path, provider, FormatProviderDisplay(provider), now);
                if (parsedBatch.Activity != null || parsedBatch.HasIdentityObservation)
                {
                    lock (_stateLock)
                    {
                        if (generation != Volatile.Read(ref _stopGeneration) || _disposed)
                            return;

                        if (_fileStates.TryGetValue(path, out var st))
                        {
                            var updated = ApplyTrackedSessionMetadataLocked(st, path, provider, parsedBatch);
                            if (updated != null)
                            {
                                anyUpdated = true;
                            }
                        }
                    }
                }
            }

            if (anyUpdated)
            {
                IReadOnlyList<AgentSnapshot>? snapshots = null;
                lock (_stateLock)
                {
                    if (!_disposed && _started && generation == Volatile.Read(ref _stopGeneration))
                    {
                        snapshots = BuildCurrentSnapshotsLocked(now);
                    }
                }

                if (snapshots != null && !_disposed && generation == Volatile.Read(ref _stopGeneration))
                {
                    ActivityChanged?.Invoke(this, snapshots);
                }
            }
        }
    }

    private void AppendOverflowReconciliationFiles(List<(string Path, string Provider)> batch)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in batch) seen.Add(item.Path);

        List<SessionWatchRoot> activeRoots;
        lock (_stateLock)
        {
            activeRoots = _watchers.Select(w => w.Root).ToList();
        }

        foreach (var root in activeRoots)
        {
            if (string.IsNullOrWhiteSpace(root.DirectoryPath) || !Directory.Exists(root.DirectoryPath))
                continue;

            try
            {
                var recentFiles = new List<(string Path, DateTime LastWriteUtc)>(MaxReconcileFilesPerRoot);
                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 4
                };

                int scanned = 0;
                foreach (string file in Directory.EnumerateFiles(root.DirectoryPath, root.SearchPattern, opts))
                {
                    scanned++;
                    if (scanned > 256) break; // Strictly bounded scan
                    DateTime lwt = File.GetLastWriteTimeUtc(file);
                    recentFiles.Add((file, lwt));
                }

                recentFiles.Sort((a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc));
                int take = Math.Min(MaxReconcileFilesPerRoot, recentFiles.Count);
                for (int i = 0; i < take; i++)
                {
                    if (seen.Add(recentFiles[i].Path))
                    {
                        batch.Add((recentFiles[i].Path, root.Provider));
                    }
                }
            }
            catch
            {
                // Non-fatal reconciliation error
            }
        }
    }

    private void PruneAndPublishIfChanged(long generation)
    {
        if (_disposed || generation != Volatile.Read(ref _stopGeneration)) return;

        DateTimeOffset now = _clock();
        IReadOnlyList<AgentSnapshot>? snapshotsToPublish = null;

        lock (_stateLock)
        {
            if (!_started || generation != Volatile.Read(ref _stopGeneration) || _fileStates.Count == 0)
                return;

            bool changed = false;
            List<string>? deadPaths = null;

            foreach (var (path, st) in _fileStates)
            {
                if (st.LatestActivity != null && IsActivityExpired(st.LatestActivity, now))
                {
                    st.LatestActivity = null;
                    changed = true;
                }

                if (st.LatestActivity == null && !File.Exists(path))
                {
                    deadPaths ??= new List<string>();
                    deadPaths.Add(path);
                }
            }

            if (deadPaths != null)
            {
                foreach (string p in deadPaths)
                {
                    _fileStates.Remove(p);
                }
            }

            if (changed)
            {
                snapshotsToPublish = BuildCurrentSnapshotsLocked(now);
            }
        }

        if (snapshotsToPublish != null && !_disposed && generation == Volatile.Read(ref _stopGeneration))
        {
            ActivityChanged?.Invoke(this, snapshotsToPublish);
        }
    }

    private bool IsActivityExpired(DerivedSessionActivity act, DateTimeOffset now)
    {
        TimeSpan age = now - act.ObservedAt;
        if (age < TimeSpan.Zero) return false;

        return act.State switch
        {
            AgentState.Done => age > _doneHoldDuration,
            AgentState.Idle => act.IsExplicitIdle ? age > _explicitIdleHoldDuration : true,
            _ => age > _workingHoldDuration
        };
    }

    private IReadOnlyList<AgentSnapshot> BuildCurrentSnapshotsLocked(DateTimeOffset now)
    {
        if (_fileStates.Count == 0)
            return Array.Empty<AgentSnapshot>();

        var result = new List<AgentSnapshot>(_fileStates.Count);
        foreach (var (filePath, st) in _fileStates)
        {
            DerivedSessionActivity? act = st.LatestActivity;
            if (act == null)
                continue;

            // Prompt freshness expiry: expired evidence is omitted so HqStateAggregator
            // falls back immediately to ProcessAgentCollector instead of masking it as Idle!
            if (IsActivityExpired(act, now))
                continue;

            string sessionId = !string.IsNullOrEmpty(st.SessionId)
                ? st.SessionId
                : ComputeSessionId(act.Provider, filePath);

            int? verifiedPid = (act.CorrelatedProcessId.HasValue && act.CorrelatedProcessId.Value > 0 &&
                                act.CorrelatedProcessStartTicks.HasValue && act.CorrelatedProcessStartTicks.Value > 0)
                ? act.CorrelatedProcessId
                : null;
            long? verifiedStartTicks = verifiedPid.HasValue ? act.CorrelatedProcessStartTicks : null;

            result.Add(new AgentSnapshot(
                Id: sessionId,
                Name: act.DisplayName,
                Role: AgentRole.External,
                SpriteKey: "generic",
                State: act.State,
                ToolName: act.DerivedActionToken,
                LastActivityAt: act.ObservedAt,
                UpdatedAt: act.ObservedAt,
                Source: "session-log",
                ProcessId: verifiedPid,
                ProcessStartTicks: verifiedStartTicks
            ));
        }

        // Order freshest-first by each session record's own event/write timestamp
        result.Sort((a, b) => (b.UpdatedAt ?? DateTimeOffset.MinValue).CompareTo(a.UpdatedAt ?? DateTimeOffset.MinValue));
        return result;
    }

    private static string FormatProviderDisplay(string provider)
    {
        return provider.Trim().ToLowerInvariant() switch
        {
            "claude" => "Claude",
            "codex" => "Codex",
            "omp" => "OMP",
            "opencode" => "OpenCode",
            _ => AgentAppearanceRegistry.FormatProviderDisplayName(provider)
        };
    }

    private static bool TryGetDirectoryIdentity(string directoryPath, out ulong volumeSerial, out ulong fileIndex)
    {
        volumeSerial = 0;
        fileIndex = 0;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(directoryPath))
            return false;

        try
        {
            using SafeFileHandle handle = CreateFileW(
                directoryPath,
                dwDesiredAccess: 0,
                dwShareMode: FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                lpSecurityAttributes: IntPtr.Zero,
                dwCreationDisposition: OPEN_EXISTING,
                dwFlagsAndAttributes: FILE_FLAG_BACKUP_SEMANTICS,
                hTemplateFile: IntPtr.Zero);

            if (handle.IsInvalid)
                return false;

            if (!GetFileInformationByHandle(handle, out var info))
                return false;

            volumeSerial = info.VolumeSerialNumber;
            fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            return fileIndex != 0;
        }
        catch
        {
            return false;
        }
    }

    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
