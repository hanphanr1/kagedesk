using System.Diagnostics;
using KageDesk.App.Audio;
using KageDesk.App.Native;
using KageDesk.App.UI;
using KageDesk.Core.Bridge;
using KageDesk.Core.Collectors;
using KageDesk.Core.Config;
using KageDesk.Core.Hooks;
using KageDesk.Core.Models;
using KageDesk.Core.State;
using KageDesk.Core.Themes;
using KageDesk.Rendering.Export;
using KageDesk.Rendering.Scenes;
using KageDesk.Rendering.Sprites;
using Microsoft.Win32;

namespace KageDesk.App.Services;

public class KageDeskRuntime : IDisposable
{
    private readonly object _configLock = new();
    private readonly ConfigStore _configStore;
    private readonly AppConfig _config;
    private readonly string _themesDir;
    private ThemeLoadResult _themeResult;
    private ThemePack _activeTheme;
    private readonly string _bridgeToken;

    private readonly HqStateAggregator _aggregator;
    private readonly OrcaCliCollector _orcaCollector;
    private ProcessAgentCollector? _procCollector;
    private SessionLogActivityCollector? _sessionLogCollector;
    private GitHubCiCollector? _gitHubCollector;
    private LocalEventBridge? _localBridge;
    private readonly MediaSessionMonitor _mediaMonitor;

    private readonly RetroSoundEngine _soundEngine;
    private readonly object _audioTrackLock = new();
    private readonly Dictionary<string, AgentState> _lastAgentStates = new(StringComparer.OrdinalIgnoreCase);
    private long _lastSeenAudioEventSeq;
    private long _lastProcessedAudioRevision;
    private int _gifExportInProgress;
    private Task? _gifExportTask;

    private readonly object _effectsLock = new();
    private MixiChillActivity? _pinnedMixiActivity;
    private string? _mixiSpeechText;
    private long _mixiPokeStartedTicks;
    private int _lastMixiPokeLineIndex = -1;
    private volatile bool _isMediaPlaying;

    private readonly DesktopOcclusionMonitor _occlusionMonitor;
    private readonly Win32WallpaperBridge _wallpaperBridge;
    private readonly UiDispatcherForm _dispatcherForm;
    private WallpaperHostForm? _wallpaperForm;
    private PetOverlayForm? _petForm;
    private TrayController? _trayController;
    private bool _displaySettingsSubscribed;

    private readonly CancellationTokenSource _cts = new();
    private Task? _pollingLoopTask;
    private volatile bool _stopping;
    private bool _disposed;
    private readonly bool _noWallpaperMode;

    private readonly object _syncLock = new();
    private int _uiSyncScheduled;
    private long _uiVersion;
    private UiSnapshot? _latestSnapshot;

    private long _processCollectorGen;

    public record UiSnapshot(
        long Version,
        HqState State,
        ThemePack Theme,
        bool WallpaperPaused,
        bool PetPaused,
        bool PetHiddenByDndOrLock);

    public AppConfig Config => _config;
    public HqStateAggregator Aggregator => _aggregator;
    public ThemePack ActiveTheme => _activeTheme;
    public IReadOnlyList<ThemePack> LoadedThemes => _themeResult.Themes;
    public UiDispatcherForm DispatcherForm => _dispatcherForm;
    public WallpaperHostForm? WallpaperForm => _wallpaperForm;
    public PetOverlayForm? PetForm => _petForm;
    public TrayController? Tray => _trayController;
    public DesktopOcclusionMonitor OcclusionMonitor => _occlusionMonitor;
    public RetroSoundEngine SoundEngine => _soundEngine;
    public MediaSessionMonitor MediaMonitor => _mediaMonitor;
    public bool WallpaperAttached => _wallpaperBridge.IsAttached;
    public string WallpaperStatusReason => _wallpaperBridge.StatusReason;
    public string BridgeToken => _bridgeToken;
    public UiSnapshot? LatestSnapshot => _latestSnapshot;

    public KageDeskRuntime(ConfigStore? configStore = null, bool noWallpaper = false)
    {
        _noWallpaperMode = noWallpaper;
        _configStore = configStore ?? new ConfigStore();
        _config = _configStore.Load();
        _bridgeToken = BridgeTokenStore.GetOrCreateToken();
        _pinnedMixiActivity = MixiChillRenderer.ParsePinnedActivity(_config.PinnedMixiActivity);

        // 1. Force-create persistent hidden top-level UiDispatcherForm HWND strictly BEFORE SessionSwitch or collectors
        _dispatcherForm = new UiDispatcherForm();
        _ = _dispatcherForm.Handle;
        _dispatcherForm.TaskbarCreated += OnExplorerRestarted;
        _dispatcherForm.WakeUpRequested += OnSingleInstanceWakeUpRequested;

        // Locate themes directory
        _themesDir = FindThemesDirectory();
        _themeResult = ThemePackLoader.LoadThemes(_themesDir);
        _activeTheme = FindActiveTheme(_config.ActiveThemeId);

        _aggregator = new HqStateAggregator(_config);
        _aggregator.SetWeatherMode(_config.WeatherMode);

        _orcaCollector = new OrcaCliCollector(_aggregator);
        _wallpaperBridge = new Win32WallpaperBridge();
        _occlusionMonitor = new DesktopOcclusionMonitor(500)
        {
            TargetMonitorDeviceName = _config.WallpaperMonitorDeviceName
        };

        _soundEngine = new RetroSoundEngine();
        _soundEngine.SetEnabled(_config.SoundEnabled);
        ApplyEffectiveAudioMute();

        _mediaMonitor = new MediaSessionMonitor(enabled: _config.EnableMediaSync);
        _mediaMonitor.PlaybackChanged += OnMediaPlaybackChanged;

        InitCollectorsAndBridge();
        InitFormsAndTray();

        _occlusionMonitor.OcclusionStateChanged += OnOcclusionStateChanged;
        _aggregator.StateChanged += OnHqStateChanged;

        // Register SessionSwitch & DisplaySettingsChanged strictly after WinForms UI dispatcher initialization
        _occlusionMonitor.SubscribeSessionSwitch();
        try
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _displaySettingsSubscribed = true;
        }
        catch { }
    }

    internal SceneEffectsState GetCurrentSceneEffects()
    {
        MixiChillActivity? pinned;
        string? speechText = null;
        float pokeElapsed = -1f;

        lock (_effectsLock)
        {
            pinned = _pinnedMixiActivity;
            if (_mixiPokeStartedTicks > 0 && !string.IsNullOrEmpty(_mixiSpeechText))
            {
                float elapsed = (float)(Stopwatch.GetTimestamp() - _mixiPokeStartedTicks) / Stopwatch.Frequency;
                if (elapsed >= 0f && elapsed < MixiChillRenderer.PokeBubbleDurationSeconds)
                {
                    speechText = _mixiSpeechText;
                    pokeElapsed = elapsed;
                }
            }
        }

        return new SceneEffectsState(
            PinnedMixiActivity: pinned,
            MixiSpeechText: speechText,
            MixiPokeElapsedSeconds: pokeElapsed,
            IsMediaPlaying: _isMediaPlaying,
            WakeUpPulseRemainingSeconds: 0f);
    }

    public void TriggerMixiPoke()
    {
        var lines = MixiChillRenderer.PokeLines;
        int nextIdx;
        lock (_effectsLock)
        {
            if (lines.Length <= 1)
            {
                nextIdx = 0;
            }
            else
            {
                nextIdx = Random.Shared.Next(lines.Length);
                if (nextIdx == _lastMixiPokeLineIndex)
                {
                    nextIdx = (nextIdx + 1) % lines.Length;
                }
            }
            _lastMixiPokeLineIndex = nextIdx;
            _mixiSpeechText = lines[nextIdx];
            _mixiPokeStartedTicks = Stopwatch.GetTimestamp();
        }

        _soundEngine.Play(SoundKind.MixiChirp);
        RequestUiSync();
    }

    public void SetPinnedMixiActivity(MixiChillActivity? activity)
    {
        lock (_effectsLock)
        {
            _pinnedMixiActivity = activity;
        }

        UpdateConfig(c => c.PinnedMixiActivity = MixiChillRenderer.ToConfigValue(activity));
        RequestUiSync();
    }

    internal void ApplyEffectiveAudioMute()
    {
        lock (_configLock)
        {
            UpdateEffectiveAudioMuteLocked();
        }
    }

    private void UpdateEffectiveAudioMuteLocked()
    {
        bool isFullscreenGameDnd = _occlusionMonitor.IsFullscreenGame;
        bool isSessionLocked = _occlusionMonitor.IsSessionLocked;
        bool effectiveMuted = !_config.SoundEnabled || isSessionLocked || (_config.HidePetsInFullscreenGame && isFullscreenGameDnd);
        _soundEngine.SetEnabled(_config.SoundEnabled);
        _soundEngine.SetMuted(effectiveMuted);
    }

    public void UpdateConfig(Action<AppConfig> updater)
    {
        ArgumentNullException.ThrowIfNull(updater);
        lock (_configLock)
        {
            updater(_config);
            _configStore.Save(_config);
            UpdateEffectiveAudioMuteLocked();
        }
    }

    public void Start()
    {
        _occlusionMonitor.Start();
        _mediaMonitor.Start();

        // Synchronous initial UI sync BEFORE presentation
        SyncUiOnDispatcher();

        // Start Pet Overlay
        if (_petForm != null && _config.PetOverlayEnabled)
        {
            _petForm.Start();
        }

        // Attach Wallpaper Form (render frame-0 synchronously before presenting, even if paused)
        if (!_noWallpaperMode && _config.WallpaperEnabled && _wallpaperForm != null)
        {
            _wallpaperForm.RenderFrameNow();
            _wallpaperBridge.AttachWallpaper(_wallpaperForm, _config.WallpaperMonitorDeviceName);
            _wallpaperForm.IsAttached = _wallpaperBridge.IsAttached;
        }

        // Start adaptive polling loop
        _pollingLoopTask = Task.Run(PollingLoopAsync);
    }

    private void InitCollectorsAndBridge()
    {
        if (_config.EnableProcessAgentCollector)
        {
            _procCollector = new ProcessAgentCollector(_aggregator, _config.EnableProcessAgentCollector);
        }

        if (_config.EnableSessionLogWatcher)
        {
            StartSessionLogCollector();
        }

        if (_config.EnableGitHubCiCollector && _config.GitHubRepos.Count > 0)
        {
            _gitHubCollector = new GitHubCiCollector(_aggregator, _config.EnableGitHubCiCollector, _config.GitHubRepos);
        }

        try
        {
            _localBridge = new LocalEventBridge(
                _aggregator,
                _config.LocalBridgePort,
                _config,
                requiredToken: _bridgeToken,
                getActiveThemeId: () => _config.ActiveThemeId);
            _localBridge.Start();
        }
        catch
        {
            // Fallback gracefully if port is unavailable
        }
    }

    private void StartSessionLogCollector()
    {
        if (_sessionLogCollector != null) return;

        var collector = new SessionLogActivityCollector();
        collector.ActivityChanged += OnSessionLogActivityChanged;
        collector.Start();
        _sessionLogCollector = collector;
    }

    private void StopSessionLogCollector()
    {
        var collector = _sessionLogCollector;
        _sessionLogCollector = null;
        if (collector != null)
        {
            collector.ActivityChanged -= OnSessionLogActivityChanged;
            collector.Dispose();
        }
        _aggregator.ClearSessionLogActivity();
    }

    private void OnSessionLogActivityChanged(object? sender, IReadOnlyList<AgentSnapshot> agents)
    {
        if (_stopping || _disposed) return;
        _aggregator.UpdateSessionLogActivity(agents);
    }

    private void OnMediaPlaybackChanged(object? sender, MediaPlaybackSnapshot snapshot)
    {
        if (_stopping || _disposed) return;
        _isMediaPlaying = snapshot.IsPlaying;
        RequestUiSync();
    }

    private WallpaperHostForm CreateWallpaperHostForm()
    {
        var form = new WallpaperHostForm(_activeTheme, _config.TargetFps, _config.ShowMixiWhenAllIdle)
        {
            GetSceneEffects = GetCurrentSceneEffects
        };
        form.MixiPoked += TriggerMixiPoke;
        return form;
    }

    private void InitFormsAndTray()
    {
        // 1. Wallpaper Host Form
        if (!_noWallpaperMode)
        {
            _wallpaperForm = CreateWallpaperHostForm();
        }

        // 2. Pet Overlay Form
        if (_config.PetOverlayEnabled)
        {
            _petForm = new PetOverlayForm(
                _activeTheme,
                _config.PetScale,
                _config.PetX,
                _config.PetIdleMode,
                _config.TargetFps,
                _config.ShowMixiWhenAllIdle,
                _config.PetMonitorDeviceName,
                _config.PetRelativeX)
            {
                GetSceneEffects = GetCurrentSceneEffects
            };
            _petForm.PetPositionChanged += newX =>
            {
                UpdateConfig(c => c.PetX = newX);
            };
            _petForm.PetPlacementChanged += (deviceName, relativeX, absoluteX) =>
            {
                UpdateConfig(c =>
                {
                    c.PetMonitorDeviceName = deviceName;
                    c.PetRelativeX = relativeX;
                    c.PetX = absoluteX;
                });
            };
            _petForm.MixiPoked += TriggerMixiPoke;
            _petForm.MixiActivityPinned += SetPinnedMixiActivity;
            _petForm.AgentClicked += agent =>
            {
                if (!string.Equals(agent.Id, "mixi-chill", StringComparison.OrdinalIgnoreCase))
                {
                    AgentDetailToast.ShowToast(agent);
                }
            };
        }

        // 3. Tray Controller
        _trayController = new TrayController(ExitApplication)
        {
            GetConfig = () => _config,
            UpdateConfig = UpdateConfig,
            GetCurrentState = () => _aggregator.CurrentState,
            GetCurrentTheme = () => _activeTheme,
            GetLoadedThemes = () => _themeResult.Themes,
            IsWallpaperAttached = () => _wallpaperBridge.IsAttached,
            GetWallpaperStatus = () => _wallpaperBridge.StatusReason,
            OnThemeChanged = SwitchTheme,
            OnReloadThemes = ReloadThemes,
            OnWeatherChanged = mode =>
            {
                UpdateConfig(c => c.WeatherMode = mode);
                _aggregator.SetWeatherMode(mode);
            },
            OnWallpaperToggled = enabled =>
            {
                if (enabled)
                {
                    if ((_wallpaperForm == null || _wallpaperForm.IsDisposed) && !_noWallpaperMode)
                    {
                        _wallpaperForm = CreateWallpaperHostForm();
                    }
                    if (_wallpaperForm != null)
                    {
                        SyncUiOnDispatcher();
                        _wallpaperForm.RenderFrameNow();
                        _wallpaperBridge.AttachWallpaper(_wallpaperForm, _config.WallpaperMonitorDeviceName);
                        _wallpaperForm.IsAttached = _wallpaperBridge.IsAttached;
                    }
                }
                else
                {
                    _wallpaperBridge.RestoreDesktopWallpaper();
                    if (_wallpaperForm != null && !_wallpaperForm.IsDisposed)
                    {
                        _wallpaperForm.IsAttached = false;
                    }
                }
            },
            OnWallpaperMonitorChanged = deviceName =>
            {
                _occlusionMonitor.TargetMonitorDeviceName = deviceName;
                if (_wallpaperBridge.IsAttached && _wallpaperForm != null && !_wallpaperForm.IsDisposed)
                {
                    _wallpaperBridge.RepositionForMonitor(deviceName);
                }
            },
            OnPetMonitorChanged = deviceName =>
            {
                _petForm?.SetMonitorAndPosition(deviceName, null, null);
            },
            OnPetModeChanged = mode =>
            {
                UpdateConfig(c => c.PetIdleMode = mode);
                _petForm?.SetIdleMode(mode);
            },
            OnPetScaleChanged = scale =>
            {
                bool ok = _petForm?.SetScale(scale) ?? true;
                if (ok)
                {
                    UpdateConfig(c => c.PetScale = scale);
                }
            },
            OnResetPetPosition = () =>
            {
                UpdateConfig(c =>
                {
                    c.PetX = null;
                    c.PetRelativeX = null;
                });
                _petForm?.SetMonitorAndPosition(_config.PetMonitorDeviceName, null, null);
            },
            OnShowMixiWhenAllIdleToggled = enabled =>
            {
                UpdateConfig(c => c.ShowMixiWhenAllIdle = enabled);
                _wallpaperForm?.SetShowMixiWhenAllIdle(enabled);
                _petForm?.SetShowMixiWhenAllIdle(enabled);
                RequestUiSync();
            },
            OnAutoPauseToggled = enabled =>
            {
                UpdateConfig(c => c.AutoPauseWallpaperWhenCovered = enabled);
                RequestUiSync();
            },
            OnGameDndToggled = enabled =>
            {
                UpdateConfig(c => c.HidePetsInFullscreenGame = enabled);
                RequestUiSync();
            },
            OnProcessAgentCollectorToggled = enabled =>
            {
                Interlocked.Increment(ref _processCollectorGen);
                UpdateConfig(c => c.EnableProcessAgentCollector = enabled);
                if (enabled)
                {
                    if (_procCollector == null)
                    {
                        _procCollector = new ProcessAgentCollector(_aggregator, true);
                    }
                    else
                    {
                        _procCollector.Enabled = true;
                    }
                }
                else
                {
                    if (_procCollector != null) _procCollector.Enabled = false;
                    _aggregator.DisableAndClearProcessAgents();
                }
            },
            OnSessionLogWatcherToggled = enabled =>
            {
                UpdateConfig(c => c.EnableSessionLogWatcher = enabled);
                if (enabled)
                {
                    StartSessionLogCollector();
                }
                else
                {
                    StopSessionLogCollector();
                }
            },
            OnGitHubCiCollectorToggled = enabled =>
            {
                UpdateConfig(c => c.EnableGitHubCiCollector = enabled);
                if (enabled)
                {
                    if (_gitHubCollector == null && _config.GitHubRepos.Count > 0)
                    {
                        _gitHubCollector = new GitHubCiCollector(_aggregator, true, _config.GitHubRepos);
                    }
                    else if (_gitHubCollector != null)
                    {
                        _gitHubCollector.Enabled = true;
                    }
                }
                else
                {
                    if (_gitHubCollector != null) _gitHubCollector.Enabled = false;
                    _aggregator.DisableAndClearCiBuilds();
                }
            },
            OnMediaSyncToggled = enabled =>
            {
                UpdateConfig(c => c.EnableMediaSync = enabled);
                _mediaMonitor.SetEnabled(enabled);
                if (!enabled)
                {
                    _isMediaPlaying = false;
                    RequestUiSync();
                }
            },
            OnSoundToggled = enabled => UpdateConfig(c => c.SoundEnabled = enabled),
            OnInstallHooks = () =>
            {
                var installer = new HookInstaller(bridgePort: _config.LocalBridgePort);
                var summary = installer.InstallAll();
                string details = string.Join(", ", summary.Results.Select(r => $"{r.Target}: {r.Status}"));
                _trayController?.ShowBalloon(
                    "KageDesk CLI Hooks",
                    summary.AllSucceeded ? $"Hooks configured ({details})." : $"Some hooks failed ({details}).",
                    summary.AllSucceeded ? ToolTipIcon.Info : ToolTipIcon.Warning,
                    4000);
            },
            OnUninstallHooks = () =>
            {
                var installer = new HookInstaller(bridgePort: _config.LocalBridgePort);
                var summary = installer.UninstallAll();
                string details = string.Join(", ", summary.Results.Select(r => $"{r.Target}: {r.Status}"));
                _trayController?.ShowBalloon(
                    "KageDesk CLI Hooks",
                    summary.AllSucceeded ? $"Hooks removed ({details})." : $"Some hooks failed ({details}).",
                    summary.AllSucceeded ? ToolTipIcon.Info : ToolTipIcon.Warning,
                    4000);
            },
            OnDemoTrigger = scenario => _aggregator.SetDemoScenario(scenario),
            OnExportGif = () =>
            {
                if (Interlocked.CompareExchange(ref _gifExportInProgress, 1, 0) != 0)
                {
                    return;
                }

                _trayController?.SetExportGifEnabled(false);

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string filename = $"KageDesk-Demo-{DateTime.Now:yyyyMMdd-HHmmss-fff}.gif";
                string gifPath = Path.Combine(desktop, filename);
                ThemePack themeToExport;
                lock (_syncLock)
                {
                    themeToExport = _activeTheme;
                }

                _gifExportTask = Task.Run(() =>
                {
                    try
                    {
                        DemoGifRecorder.RecordGif(gifPath, themeToExport, "showcase", 2, cancellationToken: _cts.Token);

                        if (!_stopping && !_disposed && _dispatcherForm.IsHandleCreated)
                        {
                            try
                            {
                                _dispatcherForm.BeginInvoke(new Action(() =>
                                {
                                    if (!_stopping && !_disposed)
                                    {
                                        _trayController?.ShowBalloon("KageDesk GIF Export", $"6-second showcase GIF exported to:\n{gifPath}", ToolTipIcon.Info);
                                    }
                                }));
                            }
                            catch { }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Clean cooperative cancellation during shutdown
                    }
                    catch (Exception ex)
                    {
                        if (!_stopping && !_disposed && _dispatcherForm.IsHandleCreated)
                        {
                            try
                            {
                                _dispatcherForm.BeginInvoke(new Action(() =>
                                {
                                    if (!_stopping && !_disposed)
                                    {
                                        _trayController?.ShowBalloon("KageDesk GIF Export Error", $"Failed to export GIF:\n{ex.Message}", ToolTipIcon.Error);
                                    }
                                }));
                            }
                            catch { }
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _gifExportInProgress, 0);
                        if (!_stopping && !_disposed && _dispatcherForm.IsHandleCreated)
                        {
                            try
                            {
                                _dispatcherForm.BeginInvoke(new Action(() =>
                                {
                                    if (!_stopping && !_disposed)
                                    {
                                        _trayController?.SetExportGifEnabled(true);
                                    }
                                }));
                            }
                            catch { }
                        }
                    }
                }, _cts.Token);
            }
        };

        if (_petForm != null)
        {
            _petForm.StudioContextMenu = _trayController.ContextMenu;
        }
    }

    private void OnSingleInstanceWakeUpRequested()
    {
        if (_stopping || _disposed) return;

        if (_dispatcherForm.IsHandleCreated)
        {
            try
            {
                _dispatcherForm.BeginInvoke(new Action(() =>
                {
                    if (_stopping || _disposed) return;

                    _petForm?.TriggerWakeUpPulse(3.0f);
                    _trayController?.ShowBalloon(
                        "KageDesk v2.2",
                        "KageDesk v2.2 is already running here \u2014 right-click the tray icon for controls.",
                        ToolTipIcon.Info,
                        3500);
                }));
            }
            catch { }
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_stopping || _disposed) return;

        if (_dispatcherForm.IsHandleCreated)
        {
            try
            {
                _dispatcherForm.BeginInvoke(new Action(() =>
                {
                    if (_stopping || _disposed) return;

                    _occlusionMonitor.TargetMonitorDeviceName = _config.WallpaperMonitorDeviceName;

                    if (_wallpaperBridge.IsAttached && _wallpaperForm != null && !_wallpaperForm.IsDisposed)
                    {
                        _wallpaperBridge.RepositionForMonitor(_config.WallpaperMonitorDeviceName);
                    }

                    _petForm?.HandleDisplaySettingsChanged();
                }));
            }
            catch { }
        }
    }

    internal static bool ExecuteExplorerRestartRecovery(
        bool hadAttachment,
        Func<bool> isHostAlive,
        Func<bool> detachHost,
        Action disposeOldForm,
        Func<bool> resetAfterDestroyedHost,
        Action markOldFormDetached,
        bool shouldAttachReplacement,
        Action createAndAttachReplacement,
        Action<string>? logError = null)
    {
        if (hadAttachment)
        {
            if (!isHostAlive())
            {
                // Old host HWND is ALREADY gone: ALWAYS dispose old form, reset bridge, and recreate fresh form
                disposeOldForm();
                if (!resetAfterDestroyedHost())
                {
                    logError?.Invoke("Explorer restart recovery aborted: ResetAfterDestroyedHost returned false for destroyed host.");
                    return false;
                }
            }
            else
            {
                // Old host HWND is still alive: try clean detach first
                bool detached = detachHost();
                if (!detached)
                {
                    disposeOldForm();
                    if (!resetAfterDestroyedHost())
                    {
                        logError?.Invoke("Explorer restart recovery aborted: Detach failed and ResetAfterDestroyedHost returned false; skipping replacement to prevent overlapping hosts.");
                        return false;
                    }
                }
                else
                {
                    markOldFormDetached();
                }
            }
        }

        if (shouldAttachReplacement)
        {
            createAndAttachReplacement();
        }

        return true;
    }

    private void OnExplorerRestarted()
    {
        if (_stopping || _disposed) return;

        if (_dispatcherForm.IsHandleCreated)
        {
            try
            {
                _dispatcherForm.BeginInvoke(new Action(() =>
                {
                    if (_stopping || _disposed) return;

                    var oldForm = _wallpaperForm;
                    bool hadAttachment = _wallpaperBridge.IsAttached || _wallpaperBridge.AttachedHwnd != IntPtr.Zero;

                    ExecuteExplorerRestartRecovery(
                        hadAttachment: hadAttachment,
                        isHostAlive: () => _wallpaperBridge.IsAttachedHostAlive(),
                        detachHost: () => _wallpaperBridge.Detach(oldForm),
                        disposeOldForm: () =>
                        {
                            if (oldForm != null && !oldForm.IsDisposed)
                            {
                                oldForm.StopRendering();
                                oldForm.Dispose();
                            }
                            _wallpaperForm = null;
                        },
                        resetAfterDestroyedHost: () => _wallpaperBridge.ResetAfterDestroyedHost(),
                        markOldFormDetached: () =>
                        {
                            if (oldForm != null && !oldForm.IsDisposed)
                            {
                                oldForm.IsAttached = false;
                            }
                        },
                        shouldAttachReplacement: _config.WallpaperEnabled && !_noWallpaperMode,
                        createAndAttachReplacement: () =>
                        {
                            if (_wallpaperForm == null || _wallpaperForm.IsDisposed)
                            {
                                _wallpaperForm = CreateWallpaperHostForm();
                            }

                            SyncUiOnDispatcher();
                            _wallpaperForm.RenderFrameNow();

                            _wallpaperBridge.AttachWallpaper(_wallpaperForm, _config.WallpaperMonitorDeviceName);
                            _wallpaperForm.IsAttached = _wallpaperBridge.IsAttached;
                        },
                        logError: msg => Trace.WriteLine($"[KageDesk] {msg}"));
                }));
            }
            catch { }
        }
    }

    private async Task PollingLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested && !_stopping)
        {
            try
            {
                if (_occlusionMonitor.IsSessionLocked)
                {
                    await Task.Delay(2000, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                await _orcaCollector.CollectOnceAsync(_cts.Token).ConfigureAwait(false);

                if (_procCollector != null && _config.EnableProcessAgentCollector)
                {
                    long procGen = Interlocked.Read(ref _processCollectorGen);
                    _procCollector.CollectOnce();
                    if (Interlocked.Read(ref _processCollectorGen) != procGen || !_config.EnableProcessAgentCollector)
                    {
                        _aggregator.DisableAndClearProcessAgents();
                    }
                }

                if (_gitHubCollector != null && _config.EnableGitHubCiCollector)
                {
                    long ciGen = _aggregator.GetCiGeneration();
                    await _gitHubCollector.CollectOnceAsync(ciGen, _cts.Token).ConfigureAwait(false);
                    if (_aggregator.GetCiGeneration() != ciGen || !_config.EnableGitHubCiCollector)
                    {
                        _aggregator.DisableAndClearCiBuilds();
                    }
                }

                // Refresh time-of-day weather & stale expirations even when no external CLI is active
                _aggregator.Reevaluate();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Silently continue polling loop
            }

            int delayMs = ComputePollingDelayMs(_config.PollingIntervalMs, _aggregator.CurrentState.Agents);

            try
            {
                await Task.Delay(delayMs, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal static int ComputePollingDelayMs(int configuredIntervalMs, IReadOnlyList<AgentSnapshot>? agents)
    {
        return Math.Max(2000, configuredIntervalMs);
    }

    private void OnHqStateChanged(object? sender, HqState state)
    {
        OnStateChangedForAudio(state);
        RequestUiSync();
    }

    internal void OnStateChangedForAudio(HqState state)
    {
        bool shouldTriggerMixiPoke = false;

        lock (_audioTrackLock)
        {
            if (state.Revision > 0 && state.Revision <= _lastProcessedAudioRevision)
            {
                return;
            }
            if (state.Revision > _lastProcessedAudioRevision)
            {
                _lastProcessedAudioRevision = state.Revision;
            }

            // 1. Process visual events (handoff, drama, celebrate, mixi_poke)
            if (state.PendingVisualEvents != null)
            {
                for (int i = 0; i < state.PendingVisualEvents.Count; i++)
                {
                    var ev = state.PendingVisualEvents[i];
                    if (ev.SequenceId > _lastSeenAudioEventSeq)
                    {
                        if (string.Equals(ev.Type, "handoff", StringComparison.OrdinalIgnoreCase))
                        {
                            _soundEngine.Play(SoundKind.Handoff);
                        }
                        else if (string.Equals(ev.Type, "review_changes", StringComparison.OrdinalIgnoreCase))
                        {
                            _soundEngine.Play(SoundKind.Drama);
                        }
                        else if (string.Equals(ev.Type, "celebrate", StringComparison.OrdinalIgnoreCase))
                        {
                            _soundEngine.Play(SoundKind.Celebrate);
                        }
                        else if (string.Equals(ev.Type, "mixi_poke", StringComparison.OrdinalIgnoreCase))
                        {
                            shouldTriggerMixiPoke = true;
                        }

                        if (ev.SequenceId > _lastSeenAudioEventSeq)
                        {
                            _lastSeenAudioEventSeq = ev.SequenceId;
                        }
                    }
                }
            }

            // 2. Process agent state transitions
            if (state.Agents != null)
            {
                var activeIds = new HashSet<string>(state.Agents.Count, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < state.Agents.Count; i++)
                {
                    var agent = state.Agents[i];
                    activeIds.Add(agent.Id);
                    if (_lastAgentStates.TryGetValue(agent.Id, out var oldState))
                    {
                        if (oldState != AgentState.NeedsApproval && agent.State == AgentState.NeedsApproval)
                        {
                            _soundEngine.Play(SoundKind.Approval);
                        }
                        else if (oldState != AgentState.Done && agent.State == AgentState.Done)
                        {
                            _soundEngine.Play(SoundKind.Done);
                        }
                        else if (oldState != AgentState.Failed && agent.State == AgentState.Failed)
                        {
                            _soundEngine.Play(SoundKind.Fail);
                        }
                    }
                    _lastAgentStates[agent.Id] = agent.State;
                }

                // Prune disappeared agent IDs so dictionary never grows unbounded over days
                if (_lastAgentStates.Count > activeIds.Count)
                {
                    List<string>? disappeared = null;
                    foreach (var id in _lastAgentStates.Keys)
                    {
                        if (!activeIds.Contains(id))
                        {
                            disappeared ??= new List<string>();
                            disappeared.Add(id);
                        }
                    }
                    if (disappeared != null)
                    {
                        for (int i = 0; i < disappeared.Count; i++)
                        {
                            _lastAgentStates.Remove(disappeared[i]);
                        }
                    }
                }
            }
        }

        if (shouldTriggerMixiPoke)
        {
            TriggerMixiPoke();
        }
    }

    internal AgentState? GetLastAgentState(string agentId)
    {
        lock (_audioTrackLock)
        {
            return _lastAgentStates.TryGetValue(agentId, out var st) ? st : null;
        }
    }

    private void OnOcclusionStateChanged(object? sender, EventArgs e)
    {
        ApplyEffectiveAudioMute();
        RequestUiSync();
    }

    public void RequestUiSync()
    {
        if (_stopping || _disposed) return;

        if (Interlocked.Exchange(ref _uiSyncScheduled, 1) == 0)
        {
            if (_dispatcherForm.IsHandleCreated && !_stopping && !_disposed)
            {
                try
                {
                    _dispatcherForm.BeginInvoke(new Action(SyncUiOnDispatcher));
                }
                catch
                {
                    Interlocked.Exchange(ref _uiSyncScheduled, 0);
                }
            }
            else
            {
                Interlocked.Exchange(ref _uiSyncScheduled, 0);
            }
        }
    }

    public void PostRenderPacket() => RequestUiSync();

    public void SyncUiOnDispatcher()
    {
        if (_stopping || _disposed) return;

        Interlocked.Exchange(ref _uiSyncScheduled, 0);

        HqState state;
        ThemePack theme;
        bool wpPaused;
        bool petPaused;
        bool petHidden;

        lock (_syncLock)
        {
            bool sessionLocked = _occlusionMonitor.IsSessionLocked;
            bool wpOccluded = _occlusionMonitor.IsWallpaperOccluded && _config.AutoPauseWallpaperWhenCovered;
            bool gameDnd = _occlusionMonitor.IsFullscreenGame && _config.HidePetsInFullscreenGame;

            wpPaused = sessionLocked || wpOccluded;
            petPaused = sessionLocked || gameDnd;
            petHidden = sessionLocked || gameDnd;

            state = _aggregator.CurrentState;
            theme = _activeTheme;

            long ver = Interlocked.Increment(ref _uiVersion);
            _latestSnapshot = new UiSnapshot(ver, state, theme, wpPaused, petPaused, petHidden);
        }

        if (_wallpaperForm != null && !_wallpaperForm.IsDisposed)
        {
            _wallpaperForm.UpdateData(state, theme);
            _wallpaperForm.IsPaused = wpPaused;
        }

        if (_petForm != null && !_petForm.IsDisposed)
        {
            _petForm.ApplyRenderState(state, theme, petPaused, petHidden);
        }
    }

    public void SwitchTheme(string themeId)
    {
        lock (_syncLock)
        {
            _activeTheme = FindActiveTheme(themeId);
        }
        UpdateConfig(c => c.ActiveThemeId = _activeTheme.Id);
        RequestUiSync();
    }

    public void ReloadThemes()
    {
        _themeResult = ThemePackLoader.LoadThemes(_themesDir);
        SwitchTheme(_config.ActiveThemeId);
    }

    private ThemePack FindActiveTheme(string themeId)
    {
        foreach (var th in _themeResult.Themes)
        {
            if (string.Equals(th.Id, themeId, StringComparison.OrdinalIgnoreCase))
            {
                return th;
            }
        }
        return _themeResult.Themes.Count > 0 ? _themeResult.Themes[0] : ThemePackLoader.BuiltInFallback;
    }

    private static string FindThemesDirectory()
    {
        string[] candidates =
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "themes"),
            Path.Combine(Directory.GetCurrentDirectory(), "themes"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "themes"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "themes")
        };

        foreach (var c in candidates)
        {
            if (Directory.Exists(c))
            {
                return Path.GetFullPath(c);
            }
        }

        return AppDomain.CurrentDomain.BaseDirectory;
    }

    private void ExitApplication()
    {
        Dispose();
        Application.Exit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;

        _cts.Cancel();

        try
        {
            _occlusionMonitor.OcclusionStateChanged -= OnOcclusionStateChanged;
            _aggregator.StateChanged -= OnHqStateChanged;
            _mediaMonitor.PlaybackChanged -= OnMediaPlaybackChanged;
            _dispatcherForm.TaskbarCreated -= OnExplorerRestarted;
            _dispatcherForm.WakeUpRequested -= OnSingleInstanceWakeUpRequested;
            _occlusionMonitor.UnsubscribeSessionSwitch();
            if (_displaySettingsSubscribed)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _displaySettingsSubscribed = false;
            }
        }
        catch { }

        StopSessionLogCollector();
        _mediaMonitor.Dispose();

        // Bounded task drain (~2s)
        if (_pollingLoopTask != null)
        {
            try
            {
                _pollingLoopTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
        }

        if (_gifExportTask != null)
        {
            try
            {
                _gifExportTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
            }
        }

        if (_localBridge != null)
        {
            try
            {
                _localBridge.StopAsync().GetAwaiter().GetResult();
            }
            catch { }
        }

        _wallpaperBridge.RestoreDesktopWallpaper();

        if (_dispatcherForm.IsHandleCreated)
        {
            try
            {
                _dispatcherForm.Invoke(new Action(() =>
                {
                    _wallpaperForm?.Dispose();
                    _petForm?.Dispose();
                    _trayController?.Dispose();
                    _dispatcherForm.Dispose();
                }));
            }
            catch
            {
                _wallpaperForm?.Dispose();
                _petForm?.Dispose();
                _trayController?.Dispose();
                _dispatcherForm.Dispose();
            }
        }
        else
        {
            _wallpaperForm?.Dispose();
            _petForm?.Dispose();
            _trayController?.Dispose();
            _dispatcherForm.Dispose();
        }

        _soundEngine.Dispose();
        _occlusionMonitor.Dispose();
        _cts.Dispose();
    }
}
