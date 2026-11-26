using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KageDesk.App.Native;

public readonly record struct MediaPlaybackSnapshot(bool IsPlaying, string? SourceApp = null);

public interface IMediaPlaybackProbe
{
    MediaPlaybackSnapshot Probe();
}

/// <summary>
/// Ultra-lightweight native Windows now-playing media sync monitor.
/// Serializes all Core Audio probing on a single dedicated MTA background thread (zero overlapping
/// timer callbacks), gated by a monotonic enableGeneration so stale samples never overwrite newer
/// state or fire after disable/re-enable.
/// Queries all active Windows Core Audio render endpoints + scalar peak meter (ZERO audio capture,
/// ZERO external dependencies, ZERO unrelated window-title reads).
/// </summary>
public sealed class MediaSessionMonitor : IDisposable
{
    private readonly IMediaPlaybackProbe _probe;
    private readonly int _pollIntervalMs;
    private readonly int _pauseHysteresisTicks;
    private readonly object _lock = new();
    private readonly AutoResetEvent _wakeSignal = new(false);

    private Thread? _mtaPollThread;
    private bool _running;
    private bool _enabled;
    private bool _disposed;
    private long _enableGeneration;
    private int _consecutiveSilentTicks;
    private MediaPlaybackSnapshot _current;

    public bool Enabled
    {
        get { lock (_lock) return _enabled; }
        set => SetEnabled(value);
    }

    public bool IsPlaying
    {
        get { lock (_lock) return _current.IsPlaying; }
    }

    public string? CurrentSource
    {
        get { lock (_lock) return _current.SourceApp; }
    }

    public MediaPlaybackSnapshot Current
    {
        get { lock (_lock) return _current; }
    }

    public long EnableGeneration
    {
        get { lock (_lock) return _enableGeneration; }
    }

    public event EventHandler<MediaPlaybackSnapshot>? PlaybackChanged;

    public MediaSessionMonitor(
        bool enabled = true,
        int pollIntervalMs = 2500,
        int pauseHysteresisTicks = 2,
        IMediaPlaybackProbe? probe = null)
    {
        _probe = probe ?? new CoreAudioMediaPlaybackProbe();
        _pollIntervalMs = Math.Max(50, pollIntervalMs);
        _pauseHysteresisTicks = Math.Max(1, pauseHysteresisTicks);
        _enabled = enabled;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || !_enabled) return;
            EnsureMtaPollThreadLocked();
            _wakeSignal.Set();
        }
    }

    public void Stop()
    {
        Thread? threadToJoin = null;
        lock (_lock)
        {
            _enableGeneration++;
            _running = false;
            threadToJoin = _mtaPollThread;
            _mtaPollThread = null;
            try { _wakeSignal.Set(); } catch (ObjectDisposedException) { }
        }

        if (threadToJoin != null && threadToJoin != Thread.CurrentThread)
        {
            try { threadToJoin.Join(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    public void SetEnabled(bool enabled)
    {
        MediaPlaybackSnapshot? toEmit = null;
        lock (_lock)
        {
            if (_disposed || _enabled == enabled) return;
            _enabled = enabled;
            _enableGeneration++;

            if (!_enabled)
            {
                _consecutiveSilentTicks = 0;
                if (_current.IsPlaying || _current.SourceApp != null)
                {
                    _current = default;
                    toEmit = _current;
                }
            }
            else
            {
                EnsureMtaPollThreadLocked();
            }

            try { _wakeSignal.Set(); } catch (ObjectDisposedException) { }
        }

        if (toEmit.HasValue)
        {
            PlaybackChanged?.Invoke(this, toEmit.Value);
        }
    }

    public MediaPlaybackSnapshot PollOnce()
    {
        long capturedGeneration;
        lock (_lock)
        {
            if (_disposed || !_enabled)
            {
                return default;
            }
            capturedGeneration = _enableGeneration;
        }

        MediaPlaybackSnapshot sample;
        try
        {
            sample = _probe.Probe();
        }
        catch
        {
            sample = default;
        }

        MediaPlaybackSnapshot? toEmit = null;
        MediaPlaybackSnapshot effective;
        lock (_lock)
        {
            // Monotonic enableGeneration gate: discard stale sample if disabled, stopped, or toggled mid-probe
            if (_disposed || !_enabled || capturedGeneration != _enableGeneration)
            {
                return default;
            }

            if (sample.IsPlaying)
            {
                _consecutiveSilentTicks = 0;
                if (!_current.IsPlaying || !string.Equals(_current.SourceApp, sample.SourceApp, StringComparison.Ordinal))
                {
                    _current = sample;
                    toEmit = _current;
                }
            }
            else
            {
                if (_current.IsPlaying)
                {
                    _consecutiveSilentTicks++;
                    if (_consecutiveSilentTicks >= _pauseHysteresisTicks)
                    {
                        _consecutiveSilentTicks = 0;
                        _current = default;
                        toEmit = _current;
                    }
                }
            }

            effective = _current;
        }

        if (toEmit.HasValue)
        {
            // Verify generation one final time before notifying listeners
            bool stillValid;
            lock (_lock)
            {
                stillValid = !_disposed && _enabled && capturedGeneration == _enableGeneration;
            }

            if (stillValid)
            {
                PlaybackChanged?.Invoke(this, toEmit.Value);
            }
        }

        return effective;
    }

    private void EnsureMtaPollThreadLocked()
    {
        if (_running && _mtaPollThread != null && _mtaPollThread.IsAlive)
        {
            return;
        }

        _running = true;
        var thread = new Thread(MtaPollLoop)
        {
            Name = "KageDesk.MediaSessionMonitor.MTA",
            IsBackground = true
        };
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.MTA);
        }

        _mtaPollThread = thread;
        thread.Start();
    }

    private void MtaPollLoop()
    {
        // Short initial delay before first probe
        try
        {
            _wakeSignal.WaitOne(Math.Min(250, _pollIntervalMs));
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        while (true)
        {
            bool shouldPoll;
            lock (_lock)
            {
                if (_disposed || !_running)
                {
                    return;
                }
                shouldPoll = _enabled;
            }

            if (shouldPoll)
            {
                PollOnce();
            }

            int waitMs;
            lock (_lock)
            {
                if (_disposed || !_running)
                {
                    return;
                }
                waitMs = _enabled ? _pollIntervalMs : Timeout.Infinite;
            }

            try
            {
                _wakeSignal.WaitOne(waitMs);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        Thread? threadToJoin;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            _running = false;
            _enableGeneration++;
            threadToJoin = _mtaPollThread;
            _mtaPollThread = null;
            try { _wakeSignal.Set(); } catch (ObjectDisposedException) { }
        }

        if (threadToJoin != null && threadToJoin != Thread.CurrentThread)
        {
            try { threadToJoin.Join(TimeSpan.FromSeconds(2)); } catch { }
        }

        _wakeSignal.Dispose();
    }
}

/// <summary>
/// Pure native Windows Core Audio session probe (no WinRT packages, no audio capture,
/// no window-title inspection). Enumerates ALL active render endpoints (headphones, HDMI,
/// speakers, USB DACs) with short-circuit on the first qualifying playing session, and keeps
/// every raw COM RCW in a strict finally release chain.
/// </summary>
public sealed class CoreAudioMediaPlaybackProbe : IMediaPlaybackProbe
{
    private const float PeakPlayingThreshold = 0.0005f;
    private const int AudioSessionStateActive = 1;
    private const int ERender = 0;
    private const uint DeviceStateActive = 0x00000001;
    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    private static readonly Dictionary<string, string> KnownMediaApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spotify"] = "Spotify",
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["firefox"] = "Firefox",
        ["brave"] = "Brave",
        ["opera"] = "Opera",
        ["vivaldi"] = "Vivaldi",
        ["vlc"] = "VLC",
        ["foobar2000"] = "foobar2000",
        ["musicbee"] = "MusicBee",
        ["applemusic"] = "Apple Music",
        ["itunes"] = "iTunes",
        ["wmplayer"] = "Media Player",
        ["microsoft.media.player"] = "Media Player",
        ["tidal"] = "TIDAL",
        ["deezer"] = "Deezer",
        ["amazonmusic"] = "Amazon Music",
        ["mpv"] = "mpv",
        ["aimp"] = "AIMP",
        ["potplayermini64"] = "PotPlayer",
        ["potplayermini"] = "PotPlayer"
    };

    public static string? MapAllowlistedMediaApp(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        return KnownMediaApps.TryGetValue(processName.Trim(), out string? display) ? display : null;
    }

    public MediaPlaybackSnapshot Probe()
    {
        object? rawEnumeratorObj = null;
        IMMDeviceCollection? deviceCollection = null;

        try
        {
            Type? enumType = Type.GetTypeFromCLSID(ClsidMMDeviceEnumerator, throwOnError: false);
            if (enumType == null) return default;

            rawEnumeratorObj = Activator.CreateInstance(enumType);
            if (rawEnumeratorObj is not IMMDeviceEnumerator enumerator)
                return default;

            int hr = enumerator.EnumAudioEndpoints(ERender, DeviceStateActive, out deviceCollection);
            if (hr != 0 || deviceCollection == null)
                return default;

            hr = deviceCollection.GetCount(out uint deviceCount);
            if (hr != 0 || deviceCount == 0)
                return default;

            string? fallbackAudioSource = null;

            for (uint d = 0; d < deviceCount; d++)
            {
                IMMDevice? device = null;
                object? rawManagerObj = null;
                IAudioSessionEnumerator? sessionEnum = null;

                try
                {
                    if (deviceCollection.Item(d, out device) != 0 || device == null)
                        continue;

                    Guid iidManager2 = IidIAudioSessionManager2;
                    hr = device.Activate(ref iidManager2, ClsCtxAll, IntPtr.Zero, out rawManagerObj);
                    if (hr != 0 || rawManagerObj is not IAudioSessionManager2 manager)
                        continue;

                    hr = manager.GetSessionEnumerator(out sessionEnum);
                    if (hr != 0 || sessionEnum == null)
                        continue;

                    hr = sessionEnum.GetCount(out int count);
                    if (hr != 0 || count <= 0)
                        continue;

                    for (int i = 0; i < count; i++)
                    {
                        IAudioSessionControl? ctrl = null;
                        try
                        {
                            if (sessionEnum.GetSession(i, out ctrl) != 0 || ctrl == null)
                                continue;

                            if (ctrl.GetState(out int state) != 0 || state != AudioSessionStateActive)
                                continue;

                            uint pid = 0;
                            if (ctrl is IAudioSessionControl2 ctrl2)
                            {
                                if (ctrl2.IsSystemSoundsSession() == 0)
                                    continue;

                                ctrl2.GetProcessId(out pid);
                                if (pid == OwnPid)
                                    continue;
                            }

                            // Verify actual audio output via OS scalar peak meter (zero audio capture!)
                            if (ctrl is IAudioMeterInformation meter &&
                                meter.GetPeakValue(out float peak) == 0 &&
                                peak > PeakPlayingThreshold)
                            {
                                string? knownApp = pid > 0 ? TryResolveAllowlistedApp(pid) : null;
                                if (knownApp != null)
                                {
                                    // Short-circuit immediately on first qualifying allowlisted media session
                                    return new MediaPlaybackSnapshot(true, knownApp);
                                }

                                fallbackAudioSource ??= "Audio";
                            }
                        }
                        finally
                        {
                            ReleaseComSafe(ctrl);
                        }
                    }
                }
                finally
                {
                    // Raw activated COM object RCW is ALWAYS released even if cast to IAudioSessionManager2 failed
                    ReleaseComSafe(sessionEnum);
                    ReleaseComSafe(rawManagerObj);
                    ReleaseComSafe(device);
                }
            }

            if (fallbackAudioSource != null)
            {
                return new MediaPlaybackSnapshot(true, fallbackAudioSource);
            }

            return default;
        }
        catch
        {
            return default;
        }
        finally
        {
            ReleaseComSafe(deviceCollection);
            ReleaseComSafe(rawEnumeratorObj);
        }
    }

    private static string? TryResolveAllowlistedApp(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return MapAllowlistedMediaApp(proc.ProcessName);
        }
        catch
        {
            return null;
        }
    }

    private static void ReleaseComSafe(object? comObj)
    {
        if (comObj != null && Marshal.IsComObject(comObj))
        {
            try
            {
                Marshal.ReleaseComObject(comObj);
            }
            catch { }
        }
    }

    private const uint ClsCtxAll = 0x17;
    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection? devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice? ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? interfacePointer);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out IntPtr audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator? sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionIndex, out IAudioSessionControl? session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int pRetVal);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl vtable slots (9 methods before IAudioSessionControl2 methods)
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam(ref Guid @override, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr newNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr newNotifications);

        // IAudioSessionControl2 methods
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetProcessId(out uint pRetVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float pfPeak);
    }
}
