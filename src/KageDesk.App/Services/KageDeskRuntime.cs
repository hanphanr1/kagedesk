using KageDesk.App.Native;
using KageDesk.App.UI;
using KageDesk.Core.Bridge;
using KageDesk.Core.Collectors;
using KageDesk.Core.Config;
using KageDesk.Core.Models;
using KageDesk.Core.State;
using KageDesk.Core.Themes;

namespace KageDesk.App.Services;

public class KageDeskRuntime : IDisposable
{
    private readonly ConfigStore _configStore;
    private readonly AppConfig _config;
    private readonly string _themesDir;
    private ThemeLoadResult _themeResult;
    private ThemePack _activeTheme;

    private readonly HqStateAggregator _aggregator;
    private readonly OrcaCliCollector _orcaCollector;
    private ProcessAgentCollector? _procCollector;
    private GitHubCiCollector? _gitHubCollector;
    private LocalEventBridge? _localBridge;

    private readonly DesktopOcclusionMonitor _occlusionMonitor;
    private readonly Win32WallpaperBridge _wallpaperBridge;
    private WallpaperHostForm? _wallpaperForm;
    private PetOverlayForm? _petForm;
    private TrayController? _trayController;

    private readonly CancellationTokenSource _cts = new();
    private Task? _pollingLoopTask;
    private bool _disposed;
    private readonly bool _noWallpaperMode;

    public AppConfig Config => _config;
    public HqStateAggregator Aggregator => _aggregator;
    public ThemePack ActiveTheme => _activeTheme;
    public IReadOnlyList<ThemePack> LoadedThemes => _themeResult.Themes;
    public WallpaperHostForm? WallpaperForm => _wallpaperForm;
    public PetOverlayForm? PetForm => _petForm;
    public TrayController? Tray => _trayController;
    public DesktopOcclusionMonitor OcclusionMonitor => _occlusionMonitor;

    public KageDeskRuntime(ConfigStore? configStore = null, bool noWallpaper = false)
    {
        _noWallpaperMode = noWallpaper;
        _configStore = configStore ?? new ConfigStore();
        _config = _configStore.Load();

        // Locate themes directory (search base directory, current directory, parent directory)
        _themesDir = FindThemesDirectory();
        _themeResult = ThemePackLoader.LoadThemes(_themesDir);
        _activeTheme = FindActiveTheme(_config.ActiveThemeId);

        _aggregator = new HqStateAggregator(_config);
        _aggregator.SetWeatherMode(_config.WeatherMode);

        _orcaCollector = new OrcaCliCollector(_aggregator);
        _wallpaperBridge = new Win32WallpaperBridge();
        _occlusionMonitor = new DesktopOcclusionMonitor(500);

        InitCollectorsAndBridge();
        InitFormsAndTray();

        _occlusionMonitor.OcclusionStateChanged += OnOcclusionStateChanged;
        _aggregator.StateChanged += OnHqStateChanged;
    }

    public void Start()
    {
        _occlusionMonitor.Start();

        // Start Pet Overlay
        if (_petForm != null && _config.PetOverlayEnabled)
        {
            _petForm.Start();
        }

        // Attach Wallpaper Form
        if (!_noWallpaperMode && _config.WallpaperEnabled && _wallpaperForm != null)
        {
            _wallpaperBridge.AttachWallpaper(_wallpaperForm);
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
            _localBridge = new LocalEventBridge(_aggregator, _config.LocalBridgePort);
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
                _config.PetX = newX;
                _configStore.Save(_config);
            };
            _petForm.AgentClicked += agent =>
            {
                AgentDetailToast.ShowToast(agent);
            };
        }

        // 3. Tray Controller
        _trayController = new TrayController(_configStore, ExitApplication)
        {
            GetCurrentState = () => _aggregator.CurrentState,
            GetCurrentTheme = () => _activeTheme,
            GetLoadedThemes = () => _themeResult.Themes,
            OnThemeChanged = SwitchTheme,
            OnReloadThemes = ReloadThemes,
            OnWeatherChanged = mode =>
            {
                _config.WeatherMode = mode;
                _aggregator.SetWeatherMode(mode);
            },
            OnWallpaperToggled = enabled =>
            {
                if (enabled)
                {
                    if (_wallpaperForm == null && !_noWallpaperMode)
                    {
                        _wallpaperForm = new WallpaperHostForm(_activeTheme, _config.TargetFps);
                    }
                    if (_wallpaperForm != null)
                    {
                        _wallpaperBridge.AttachWallpaper(_wallpaperForm);
                    }
                }
                else
                {
                    _wallpaperBridge.RestoreDesktopWallpaper();
                }
            },
            OnPetModeChanged = mode =>
            {
                _config.PetIdleMode = mode;
                _petForm?.SetIdleMode(mode);
            },
            OnPetScaleChanged = scale =>
            {
                _config.PetScale = scale;
                _petForm?.SetScale(scale);
            },
            OnResetPetPosition = () =>
            {
                _config.PetX = null;
                _petForm?.SetPetX(null);
            },
            OnAutoPauseToggled = enabled =>
            {
                _config.AutoPauseWallpaperWhenCovered = enabled;
                UpdateOcclusionPause();
            },
            OnGameDndToggled = enabled =>
            {
                _config.HidePetsInFullscreenGame = enabled;
                UpdateOcclusionPause();
            },
            OnProcessAgentCollectorToggled = enabled =>
            {
                _config.EnableProcessAgentCollector = enabled;
                if (enabled && _procCollector == null)
                {
                    _procCollector = new ProcessAgentCollector(_aggregator, true);
                }
                else if (_procCollector != null)
                {
                    _procCollector.Enabled = enabled;
                }
            },
            OnGitHubCiCollectorToggled = enabled =>
            {
                _config.EnableGitHubCiCollector = enabled;
                if (enabled && _gitHubCollector == null && _config.GitHubRepos.Count > 0)
                {
                    _gitHubCollector = new GitHubCiCollector(_aggregator, true, _config.GitHubRepos);
                }
                else if (_gitHubCollector != null)
                {
                    _gitHubCollector.Enabled = enabled;
                }
            },
            OnDemoTrigger = TriggerDemoState
        };

        if (_petForm != null)
        {
            _petForm.StudioContextMenu = _trayController.ContextMenu;
        }
    }

    private async Task PollingLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // If session is locked, wait until unlocked
                if (_occlusionMonitor.IsSessionLocked)
                {
                    await Task.Delay(2000, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                // Collect Orca CLI
                await _orcaCollector.CollectOnceAsync(_cts.Token).ConfigureAwait(false);

                // Collect Process Agents if enabled
                if (_procCollector != null && _config.EnableProcessAgentCollector)
                {
                    _procCollector.CollectOnce();
                }

                // Collect GitHub CI if enabled
                if (_gitHubCollector != null && _config.EnableGitHubCiCollector)
                {
                    await _gitHubCollector.CollectOnceAsync(_cts.Token).ConfigureAwait(false);
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

            // Adaptive back-off:
            // When any agent is Working or NeedsApproval: poll at Config.PollingIntervalMs (min 2000ms)
            // When all agents are Idle: back off to 6000ms
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
        _wallpaperForm?.UpdateData(state, _activeTheme);
        _petForm?.UpdateData(state, _activeTheme);
    }

    private void OnOcclusionStateChanged(object? sender, EventArgs e)
    {
        UpdateOcclusionPause();
    }

    private void UpdateOcclusionPause()
    {
        bool sessionLocked = _occlusionMonitor.IsSessionLocked;
        bool wallpaperOccluded = _occlusionMonitor.IsWallpaperOccluded && _config.AutoPauseWallpaperWhenCovered;
        bool gameDnd = _occlusionMonitor.IsFullscreenGame && _config.HidePetsInFullscreenGame;

        if (_wallpaperForm != null)
        {
            _wallpaperForm.IsPaused = sessionLocked || wallpaperOccluded;
        }

        if (_petForm != null)
        {
            _petForm.IsPaused = sessionLocked || gameDnd;
        }
    }

    public void SwitchTheme(string themeId)
    {
        _activeTheme = FindActiveTheme(themeId);
        _wallpaperForm?.UpdateData(_aggregator.CurrentState, _activeTheme);
        _petForm?.UpdateData(_aggregator.CurrentState, _activeTheme);
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

    private void TriggerDemoState(string action)
    {
        var now = DateTimeOffset.UtcNow;
        var accounts = _aggregator.CurrentState.Accounts;
        var builds = _aggregator.CurrentState.Builds;

        switch (action)
        {
            case "approval":
                var approvalAgents = new List<AgentSnapshot>
                {
                    new("demo-leader", "Shikamaru (Leader)", AgentRole.Leader, "shikamaru", AgentState.NeedsApproval,
                        QuestionPrompt: "Approve deploy KageDesk v1.2 to production?", TaskTitle: "Release Gate", Repo: "kagedesk"),
                    new("demo-coder", "Minato (Coder)", AgentRole.Coder, "minato", AgentState.Working, ToolName: "edit", Repo: "kagedesk"),
                    new("demo-tester", "Tsunade (Tester)", AgentRole.Tester, "tsunade", AgentState.Idle, Repo: "kagedesk")
                };
                _aggregator.UpdateOrcaSnapshot(approvalAgents, accounts);
                break;

            case "done":
                var doneAgents = new List<AgentSnapshot>
                {
                    new("demo-leader", "Shikamaru (Leader)", AgentRole.Leader, "shikamaru", AgentState.Done, TaskTitle: "All tests passing!", Repo: "kagedesk"),
                    new("demo-coder", "Minato (Coder)", AgentRole.Coder, "minato", AgentState.Idle, Repo: "kagedesk"),
                    new("demo-tester", "Tsunade (Tester)", AgentRole.Tester, "tsunade", AgentState.Idle, Repo: "kagedesk")
                };
                _aggregator.UpdateOrcaSnapshot(doneAgents, accounts);
                break;

            case "failed":
                var failAgents = new List<AgentSnapshot>
                {
                    new("demo-coder", "Minato (Coder)", AgentRole.Coder, "minato", AgentState.Failed,
                        QuestionPrompt: "Build failed on main with exit code 1", TaskTitle: "Compilation Error", Repo: "kagedesk"),
                    new("demo-leader", "Shikamaru (Leader)", AgentRole.Leader, "shikamaru", AgentState.Working, ToolName: "diagnose", Repo: "kagedesk")
                };
                _aggregator.UpdateOrcaSnapshot(failAgents, accounts);
                break;

            case "clones":
                var cloneAgents = new List<AgentSnapshot>
                {
                    new("ag-1", "Shikamaru", AgentRole.Leader, "shikamaru", AgentState.Working, ToolName: "plan", Repo: "kagedesk"),
                    new("ag-2", "Minato", AgentRole.Coder, "minato", AgentState.Working, ToolName: "compile", Repo: "kagedesk"),
                    new("ag-3", "Tsunade", AgentRole.Tester, "tsunade", AgentState.Working, ToolName: "test", Repo: "kagedesk"),
                    new("ag-4", "Kakashi", AgentRole.Reviewer, "kakashi", AgentState.Working, ToolName: "review", Repo: "kagedesk"),
                    new("ag-5", "Clone 1", AgentRole.Coder, "minato", AgentState.Working, ToolName: "worker_done", Repo: "kagedesk"),
                    new("ag-6", "Clone 2", AgentRole.Coder, "minato", AgentState.Working, ToolName: "benchmark", Repo: "kagedesk")
                };
                _aggregator.UpdateOrcaSnapshot(cloneAgents, accounts);
                break;

            case "reset":
                _ = Task.Run(async () =>
                {
                    await _orcaCollector.CollectOnceAsync().ConfigureAwait(false);
                });
                break;
        }
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

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "themes");
    }

    public void ExitApplication()
    {
        Dispose();
        Application.Exit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        try
        {
            _occlusionMonitor.OcclusionStateChanged -= OnOcclusionStateChanged;
            _aggregator.StateChanged -= OnHqStateChanged;
        }
        catch { }

        _wallpaperBridge.RestoreDesktopWallpaper();

        _wallpaperForm?.Dispose();
        _petForm?.Dispose();
        _trayController?.Dispose();
        _occlusionMonitor.Dispose();
        _localBridge?.Dispose();
        _cts.Dispose();
    }
}
