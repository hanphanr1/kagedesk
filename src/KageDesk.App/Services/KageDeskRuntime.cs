using System.Diagnostics;
using KageDesk.App.Audio;
using KageDesk.App.Native;
using KageDesk.App.UI;
using KageDesk.Core.Bridge;
using KageDesk.Core.Collectors;
using KageDesk.Core.Config;
using KageDesk.Core.Models;
using KageDesk.Core.State;
using KageDesk.Core.Themes;
using KageDesk.Rendering.Export;

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
    private GitHubCiCollector? _gitHubCollector;
    private LocalEventBridge? _localBridge;

    private readonly RetroSoundEngine _soundEngine;
    private readonly object _audioTrackLock = new();
    private readonly Dictionary<string, AgentState> _lastAgentStates = new(StringComparer.OrdinalIgnoreCase);
    private long _lastSeenAudioEventSeq;
    private int _gifExportInProgress;
    private Task? _gifExportTask;

    private readonly DesktopOcclusionMonitor _occlusionMonitor;
    private readonly Win32WallpaperBridge _wallpaperBridge;
    private readonly UiDispatcherForm _dispatcherForm;
    private WallpaperHostForm? _wallpaperForm;
    private PetOverlayForm? _petForm;
    private TrayController? _trayController;

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

        // 1. Force-create persistent hidden top-level UiDispatcherForm HWND strictly BEFORE SessionSwitch or collectors
        _dispatcherForm = new UiDispatcherForm();
        _ = _dispatcherForm.Handle;
        _dispatcherForm.TaskbarCreated += OnExplorerRestarted;

        // Locate themes directory
        _themesDir = FindThemesDirectory();
        _themeResult = ThemePackLoader.LoadThemes(_themesDir);
        _activeTheme = FindActiveTheme(_config.ActiveThemeId);

        _aggregator = new HqStateAggregator(_config);
        _aggregator.SetWeatherMode(_config.WeatherMode);

        _orcaCollector = new OrcaCliCollector(_aggregator);
        _wallpaperBridge = new Win32WallpaperBridge();
        _occlusionMonitor = new DesktopOcclusionMonitor(500);

        _soundEngine = new RetroSoundEngine
        {
            IsSoundEnabled = () => _config.SoundEnabled,
            IsFullscreenGameDnd = () => _occlusionMonitor.IsFullscreenGame && _config.HidePetsInFullscreenGame,
            IsSessionLocked = () => _occlusionMonitor.IsSessionLocked
        };
        _soundEngine.SetEnabled(_config.SoundEnabled);
        _soundEngine.SetMuted(_occlusionMonitor.IsSessionLocked || (_occlusionMonitor.IsFullscreenGame && _config.HidePetsInFullscreenGame));

        InitCollectorsAndBridge();
        InitFormsAndTray();

        _occlusionMonitor.OcclusionStateChanged += OnOcclusionStateChanged;
        _aggregator.StateChanged += OnHqStateChanged;

        // Register SessionSwitch strictly after WinForms UI dispatcher initialization
        _occlusionMonitor.SubscribeSessionSwitch();
    }

    public void UpdateConfig(Action<AppConfig> updater)
    {
        ArgumentNullException.ThrowIfNull(updater);
        bool soundEnabled;
        lock (_configLock)
        {
            updater(_config);
            _configStore.Save(_config);
            soundEnabled = _config.SoundEnabled;
        }
        _soundEngine.SetEnabled(soundEnabled);
    }

    public void Start()
    {
        _occlusionMonitor.Start();

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
            _wallpaperBridge.AttachWallpaper(_wallpaperForm);
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

    private void InitFormsAndTray()
    {
        // 1. Wallpaper Host Form
        if (!_noWallpaperMode)
        {
            _wallpaperForm = new WallpaperHostForm(_activeTheme, _config.TargetFps);
        }

        // 2. Pet Overlay Form
        if (_config.PetOverlayEnabled)
        {
            _petForm = new PetOverlayForm(_activeTheme, _config.PetScale, _config.PetX, _config.PetIdleMode, _config.TargetFps);
            _petForm.PetPositionChanged += newX =>
            {
                UpdateConfig(c => c.PetX = newX);
            };
            _petForm.AgentClicked += agent =>
            {
                AgentDetailToast.ShowToast(agent);
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
                        _wallpaperForm = new WallpaperHostForm(_activeTheme, _config.TargetFps);
                    }
                    if (_wallpaperForm != null)
                    {
                        SyncUiOnDispatcher();
                        _wallpaperForm.RenderFrameNow();
                        _wallpaperBridge.AttachWallpaper(_wallpaperForm);
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
                UpdateConfig(c => c.PetX = null);
                _petForm?.SetPetX(null);
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
            OnSoundToggled = enabled => UpdateConfig(c => c.SoundEnabled = enabled),
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
                        DemoGifRecorder.RecordGif(gifPath, themeToExport, "showcase", 2);

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
                                _wallpaperForm = new WallpaperHostForm(_activeTheme, _config.TargetFps);
                            }

                            SyncUiOnDispatcher();
                            _wallpaperForm.RenderFrameNow();

                            _wallpaperBridge.AttachWallpaper(_wallpaperForm);
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
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Silently continue polling loop
            }

            int delayMs = 6000;
            var currentAgents = _aggregator.CurrentState.Agents;
            for (int i = 0; i < currentAgents.Count; i++)
            {
                if (currentAgents[i].State is AgentState.Working or AgentState.NeedsApproval)
                {
                    delayMs = Math.Max(2000, _config.PollingIntervalMs);
                    break;
                }
            }

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

    private void OnHqStateChanged(object? sender, HqState state)
    {
        ProcessAudioEvents(state);
        RequestUiSync();
    }

    private void ProcessAudioEvents(HqState state)
    {
        lock (_audioTrackLock)
        {
            // 1. Process visual events (handoff, etc.)
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
    }

    private void OnOcclusionStateChanged(object? sender, EventArgs e)
    {
        bool muted = _occlusionMonitor.IsSessionLocked || (_occlusionMonitor.IsFullscreenGame && _config.HidePetsInFullscreenGame);
        _soundEngine.SetMuted(muted);
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
            _occlusionMonitor.UnsubscribeSessionSwitch();
        }
        catch { }

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
                _gifExportTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
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
