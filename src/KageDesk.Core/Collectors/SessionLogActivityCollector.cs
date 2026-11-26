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
    int? CorrelatedProcessId = null
);

/// <summary>
/// Lightweight, privacy-safe local session/transcript log watcher.
/// Uses a single serialized bounded consumer over FileSystemWatcher events with bounded tail reads (&lt;= 8 KB),
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

    private sealed class TrackedFileState
    {
        public long LastCompleteRecordOffset;
        public bool AtRecordBoundary;
        public DateTime CreationTimeUtc;
        public ulong VolumeSerialNumber;
        public ulong FileIndex;
        public byte[]? HeaderFingerprint;
        public int HeaderFingerprintLength;
        public DateTime LastSeenUtc;
        public string Provider = string.Empty;
        public DerivedSessionActivity? LatestActivity;
    }

    private sealed class ActiveWatcherEntry
    {
        public required SessionWatchRoot Root { get; init; }
        public required FileSystemWatcher Watcher { get; init; }
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
    private readonly Dictionary<string, DerivedSessionActivity> _providerActivity = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _debounceTimer;
    private Timer? _maintenanceTimer;
    private DateTimeOffset? _firstPendingAt;
    private bool _overflowPending;
    private bool _immediateTimerArmed;
    private bool _consumerScheduledOrRunning;
    private readonly ManualResetEventSlim _consumerIdleEvent = new(initialState: true);

    private long _stopGeneration;
    private long _removalEpoch;
    private bool _started;
    private bool _disposed;

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
            _providerActivity.Clear();
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
    /// Synchronously processes a single file delta using bounded tail reading (&lt;= 8 KB)
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

        DerivedSessionActivity? derived = ReadAndParseFileDeltaInPlace(filePath, provider, resolvedDisplay, now);
        if (derived == null)
            return null;

        IReadOnlyList<AgentSnapshot>? snapshots = null;
        lock (_stateLock)
        {
            if (generation != Volatile.Read(ref _stopGeneration) || _disposed)
            {
                return null;
            }

            if (_fileStates.TryGetValue(filePath, out var st))
            {
                st.Provider = provider;
                st.LatestActivity = derived;
            }

            RecomputeProviderActivityLocked(provider, now);
            snapshots = BuildCurrentSnapshotsLocked(now);
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
    /// Reads at most <see cref="MaxTailReadBytes"/> complete newline-terminated UTF-8 bytes
    /// from the file's previous committed record boundary (or bounded tail on initial &gt;8 KB seek / rotation).
    /// </summary>
    public byte[]? ReadBoundedTailDelta(string filePath)
    {
        lock (_readBufferLock)
        {
            try
            {
                int count = ReadCompleteRecordsIntoBufferLocked(filePath, out int sliceStart);
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
    /// Internal test hook to trigger bounded missing-root reconciliation synchronously.
    /// </summary>
    internal void ReconcileMissingRootsNow()
    {
        lock (_stateLock)
        {
            if (_disposed || !_started) return;
            AttachMissingRootsLocked();
        }
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

            if (_fileStates.TryGetValue(fullPath, out var removed))
            {
                _fileStates.Remove(fullPath);
                if (!string.IsNullOrEmpty(removed.Provider))
                {
                    RecomputeProviderActivityLocked(removed.Provider, now);
                    snapshots = BuildCurrentSnapshotsLocked(now);
                }
            }
        }

        if (snapshots != null && generation == Volatile.Read(ref _stopGeneration) && !_disposed)
        {
            ActivityChanged?.Invoke(this, snapshots);
        }
    }

    private DerivedSessionActivity? ReadAndParseFileDeltaInPlace(
        string filePath,
        string provider,
        string displayName,
        DateTimeOffset now)
    {
        lock (_readBufferLock)
        {
            try
            {
                int count = ReadCompleteRecordsIntoBufferLocked(filePath, out int sliceStart);
                if (count <= 0) return null;

                return ParseDerivedActivityFromTail(
                    provider,
                    displayName,
                    new ReadOnlySpan<byte>(_readBuffer, sliceStart, count),
                    now);
            }
            finally
            {
                // Always clear raw transcript bytes from reusable buffer immediately after parsing
                Array.Clear(_readBuffer, 0, _readBuffer.Length);
                Array.Clear(_headerProbeBuffer, 0, _headerProbeBuffer.Length);
            }
        }
    }

    private int ReadCompleteRecordsIntoBufferLocked(string filePath, out int sliceStart)
    {
        sliceStart = 0;
        long removalEpochBeforeRead = Volatile.Read(ref _removalEpoch);
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            if (length <= 0) return 0;

            FileIdentity identity = ReadFileIdentity(fs, length);

            long startOffset;
            bool atRecordBoundary;

            lock (_stateLock)
            {
                if (_fileStates.TryGetValue(filePath, out var prev) &&
                    !IsFileRotatedOrReplaced(prev, identity, length))
                {
                    if (prev.LastCompleteRecordOffset == length)
                    {
                        return 0;
                    }

                    long delta = length - prev.LastCompleteRecordOffset;
                    if (delta <= MaxTailReadBytes)
                    {
                        startOffset = prev.LastCompleteRecordOffset;
                        atRecordBoundary = prev.AtRecordBoundary;
                    }
                    else
                    {
                        startOffset = length - MaxTailReadBytes;
                        atRecordBoundary = false;
                    }
                }
                else
                {
                    // Brand-new file OR rotated/replaced/truncated file
                    startOffset = Math.Max(0, length - MaxTailReadBytes);
                    atRecordBoundary = startOffset == 0;
                }
            }

            int toRead = (int)Math.Min(MaxTailReadBytes, length - startOffset);
            if (toRead <= 0) return 0;

            fs.Seek(startOffset, SeekOrigin.Begin);
            int totalRead = 0;
            while (totalRead < toRead)
            {
                int read = fs.Read(_readBuffer, totalRead, toRead - totalRead);
                if (read <= 0) break;
                totalRead += read;
            }

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
                    long newOffset = totalRead >= MaxTailReadBytes ? startOffset + totalRead : startOffset;
                    CommitFileState(filePath, identity, newOffset, atRecordBoundary: false, removalEpochBeforeRead);
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
                    CommitFileState(filePath, identity, startOffset + parseStart, atRecordBoundary: true, removalEpochBeforeRead);
                }
                else if (totalRead >= MaxTailReadBytes)
                {
                    // Guard against an unbounded >8 KB malformed line without any '\n'
                    CommitFileState(filePath, identity, startOffset + totalRead, atRecordBoundary: false, removalEpochBeforeRead);
                }
                else
                {
                    // Incomplete trailing line / split UTF-8 byte sequence: do NOT advance committed offset!
                    CommitFileState(filePath, identity, startOffset, atRecordBoundary: boundaryAtParseStart, removalEpochBeforeRead);
                }

                return 0;
            }

            int completeLen = (lastLf + 1) - parseStart;
            long committedOffset = startOffset + lastLf + 1;
            if (!CommitFileState(filePath, identity, committedOffset, atRecordBoundary: true, removalEpochBeforeRead))
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
        ulong VolumeSerialNumber,
        ulong FileIndex,
        byte[] HeaderFingerprint,
        int HeaderFingerprintLength
    );

    private FileIdentity ReadFileIdentity(FileStream fs, long length)
    {
        DateTime creationUtc = DateTime.MinValue;
        ulong volumeSerial = 0;
        ulong fileIndex = 0;

        if (OperatingSystem.IsWindows())
        {
            if (GetFileInformationByHandle(fs.SafeFileHandle, out var info))
            {
                long ft = ((long)info.CreationTimeHigh << 32) | info.CreationTimeLow;
                if (ft > 0)
                {
                    creationUtc = DateTime.FromFileTimeUtc(ft);
                }
                volumeSerial = info.VolumeSerialNumber;
                fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            }
        }

        int probeLen = (int)Math.Min(HeaderFingerprintBytes, length);
        fs.Seek(0, SeekOrigin.Begin);
        int read = 0;
        while (read < probeLen)
        {
            int n = fs.Read(_headerProbeBuffer, read, probeLen - read);
            if (n <= 0) break;
            read += n;
        }

        byte[] hash = SHA256.HashData(new ReadOnlySpan<byte>(_headerProbeBuffer, 0, read));
        return new FileIdentity(creationUtc, volumeSerial, fileIndex, hash, read);
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

    private bool CommitFileState(string filePath, FileIdentity identity, long offset, bool atRecordBoundary, long removalEpochBeforeRead)
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
    /// </summary>
    public static DerivedSessionActivity? ParseDerivedActivityFromTail(
        string provider,
        string displayName,
        ReadOnlySpan<byte> tailBytes,
        DateTimeOffset observedAt)
    {
        if (tailBytes.IsEmpty)
            return null;

        DerivedSessionActivity? latest = null;
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

            var parsed = TryParseSingleJsonLineUtf8(provider, displayName, line, observedAt);
            if (parsed != null)
            {
                latest = parsed;
            }
        }

        return latest;
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

    private static DerivedSessionActivity? TryParseSingleJsonLineUtf8(
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
                return null;

            bool isApproval = false;
            bool isCompletion = false;
            bool isExplicitIdle = false;
            bool isWorkingSignal = false;
            string? canonicalToolToken = null;
            int? correlatedPid = null;

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
                else if (reader.ValueTextEquals("pid"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int pid) && pid > 0)
                    {
                        correlatedPid = pid;
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
                        ParsePayloadObjectUtf8(ref reader, ref isApproval, ref isCompletion, ref isExplicitIdle, ref isWorkingSignal, ref canonicalToolToken);
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

            if (isApproval)
            {
                return new DerivedSessionActivity(provider, displayName, AgentState.NeedsApproval, "approve", observedAt, IsExplicitIdle: false, CorrelatedProcessId: correlatedPid);
            }

            if (canonicalToolToken != null)
            {
                return new DerivedSessionActivity(provider, displayName, AgentState.Working, canonicalToolToken, observedAt, IsExplicitIdle: false, CorrelatedProcessId: correlatedPid);
            }

            if (isCompletion)
            {
                return new DerivedSessionActivity(provider, displayName, AgentState.Done, null, observedAt, IsExplicitIdle: false, CorrelatedProcessId: correlatedPid);
            }

            if (isExplicitIdle)
            {
                return new DerivedSessionActivity(provider, displayName, AgentState.Idle, null, observedAt, IsExplicitIdle: true, CorrelatedProcessId: correlatedPid);
            }

            if (isWorkingSignal)
            {
                return new DerivedSessionActivity(provider, displayName, AgentState.Working, "think", observedAt, IsExplicitIdle: false, CorrelatedProcessId: correlatedPid);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
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
        ref string? canonicalToolToken)
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
            watcher = new FileSystemWatcher(root.DirectoryPath, root.SearchPattern)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                InternalBufferSize = 16384
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
            watcher.Error += (_, _) =>
            {
                if (_disposed) return;
                lock (_stateLock)
                {
                    if (_disposed || !_started) return;
                    _overflowPending = true;
                    ScheduleConsumerLocked(_clock(), forceImmediate: false);
                }
            };
            watcher.EnableRaisingEvents = true;
            return new ActiveWatcherEntry { Root = root, Watcher = watcher };
        }
        catch
        {
            DisposeWatcherSafe(watcher);
            return null;
        }
    }

    private static void DisposeWatcherSafe(FileSystemWatcher? watcher)
    {
        if (watcher == null) return;
        try { watcher.EnableRaisingEvents = false; } catch { }
        try { watcher.Dispose(); } catch { }
    }

    private void AttachMissingRootsLocked()
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

        try
        {
            RunSerializedConsumerLoop(generation);
        }
        finally
        {
            bool needReschedule = false;
            lock (_stateLock)
            {
                _consumerScheduledOrRunning = false;
                if (!_disposed && _started && (_pendingPaths.Count > 0 || _overflowPending))
                {
                    needReschedule = true;
                    ScheduleConsumerLocked(_clock(), forceImmediate: false);
                }
                _consumerIdleEvent.Set();
            }

            if (needReschedule)
            {
                // Timer has already been armed under lock
            }
        }
    }

    private void RunSerializedConsumerLoop(long generation)
    {
        while (!_disposed && generation == Volatile.Read(ref _stopGeneration))
        {
            List<(string Path, string Provider)> batch;
            bool runOverflowReconcile;

            lock (_stateLock)
            {
                if (!_started || generation != Volatile.Read(ref _stopGeneration))
                    return;

                runOverflowReconcile = _overflowPending;
                _overflowPending = false;
                _firstPendingAt = null;
                _immediateTimerArmed = false;

                if (_pendingPaths.Count == 0 && !runOverflowReconcile)
                    return;

                batch = new List<(string, string)>(_pendingPaths.Count);
                foreach (var kv in _pendingPaths)
                {
                    batch.Add((kv.Key, kv.Value));
                }
                _pendingPaths.Clear();
            }

            if (runOverflowReconcile)
            {
                AppendOverflowReconciliationFiles(batch);
            }

            if (batch.Count == 0) continue;

            DateTimeOffset now = _clock();
            bool anyUpdated = false;
            var touchedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                            touchedProviders.Add(provider);
                            anyUpdated = true;
                        }
                    }
                    continue;
                }

                var derived = ReadAndParseFileDeltaInPlace(path, provider, FormatProviderDisplay(provider), now);
                if (derived != null)
                {
                    lock (_stateLock)
                    {
                        if (generation != Volatile.Read(ref _stopGeneration) || _disposed)
                            return;

                        if (_fileStates.TryGetValue(path, out var st))
                        {
                            st.Provider = provider;
                            st.LatestActivity = derived;
                        }
                        touchedProviders.Add(provider);
                        anyUpdated = true;
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
                        foreach (string p in touchedProviders)
                        {
                            RecomputeProviderActivityLocked(p, now);
                        }
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

    private void RunMaintenanceTick()
    {
        long generation;
        lock (_stateLock)
        {
            if (_disposed || !_started) return;
            AttachMissingRootsLocked();
            if (_consumerScheduledOrRunning) return;
            _consumerScheduledOrRunning = true;
            _consumerIdleEvent.Reset();
            generation = Volatile.Read(ref _stopGeneration);
        }

        try
        {
            PruneAndPublishIfChanged(generation);
        }
        finally
        {
            lock (_stateLock)
            {
                _consumerScheduledOrRunning = false;
                _consumerIdleEvent.Set();
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
            if (!_started || generation != Volatile.Read(ref _stopGeneration) || _providerActivity.Count == 0)
                return;

            bool changed = false;
            var toRemove = new List<string>();
            foreach (var (provider, act) in _providerActivity)
            {
                if (IsActivityExpired(act, now))
                {
                    toRemove.Add(provider);
                    changed = true;
                }
            }

            foreach (var k in toRemove)
            {
                _providerActivity.Remove(k);
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

    private void RecomputeProviderActivityLocked(string provider, DateTimeOffset now)
    {
        DerivedSessionActivity? newest = null;
        foreach (var st in _fileStates.Values)
        {
            if (!string.Equals(st.Provider, provider, StringComparison.OrdinalIgnoreCase)) continue;
            if (st.LatestActivity == null) continue;
            if (IsActivityExpired(st.LatestActivity, now)) continue;

            if (newest == null || st.LatestActivity.ObservedAt >= newest.ObservedAt)
            {
                newest = st.LatestActivity;
            }
        }

        if (newest != null)
        {
            _providerActivity[provider] = newest;
        }
        else
        {
            _providerActivity.Remove(provider);
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
        if (_providerActivity.Count == 0)
            return Array.Empty<AgentSnapshot>();

        var result = new List<AgentSnapshot>(_providerActivity.Count);
        foreach (var (provider, act) in _providerActivity)
        {
            // Prompt freshness expiry: expired evidence is omitted so HqStateAggregator
            // falls back immediately to ProcessAgentCollector instead of masking it as Idle!
            if (IsActivityExpired(act, now))
                continue;

            result.Add(new AgentSnapshot(
                Id: $"session:{provider}",
                Name: act.DisplayName,
                Role: AgentRole.External,
                SpriteKey: "generic",
                State: act.State,
                ToolName: act.DerivedActionToken,
                LastActivityAt: act.ObservedAt,
                UpdatedAt: act.ObservedAt,
                Source: "session-log",
                ProcessId: act.CorrelatedProcessId
            ));
        }

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
}
