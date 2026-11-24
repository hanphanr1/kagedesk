using System.Diagnostics;
using System.Drawing;
using KageDesk.App.Native;
using KageDesk.Core.Config;
using KageDesk.Core.Models;
using KageDesk.Core.Themes;

namespace KageDesk.App.UI;

public class TrayController : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _contextMenu;
    private readonly Action _onExit;

    private IntPtr _hIcon = IntPtr.Zero;
    private Icon? _managedIcon;
    private bool _disposed;

    // References to runtime services
    public Func<AppConfig>? GetConfig { get; set; }
    public Action<Action<AppConfig>>? UpdateConfig { get; set; }
    public Func<HqState>? GetCurrentState { get; set; }
    public Func<ThemePack>? GetCurrentTheme { get; set; }
    public Func<IReadOnlyList<ThemePack>>? GetLoadedThemes { get; set; }
    public Func<bool>? IsWallpaperAttached { get; set; }
    public Func<string>? GetWallpaperStatus { get; set; }

    public Action<string>? OnThemeChanged { get; set; }
    public Action? OnReloadThemes { get; set; }
    public Action<WeatherMode>? OnWeatherChanged { get; set; }
    public Action<bool>? OnWallpaperToggled { get; set; }
    public Action<string>? OnPetModeChanged { get; set; }
    public Action<int>? OnPetScaleChanged { get; set; }
    public Action? OnResetPetPosition { get; set; }
    public Action<bool>? OnAutoPauseToggled { get; set; }
    public Action<bool>? OnGameDndToggled { get; set; }
    public Action<bool>? OnProcessAgentCollectorToggled { get; set; }
    public Action<bool>? OnGitHubCiCollectorToggled { get; set; }
    public Action<string>? OnDemoTrigger { get; set; }

    public ContextMenuStrip ContextMenu => _contextMenu ?? throw new ObjectDisposedException(nameof(TrayController));

    public TrayController(Action onExit)
    {
        _onExit = onExit ?? throw new ArgumentNullException(nameof(onExit));

        try
        {
            _contextMenu = new ContextMenuStrip();
            _notifyIcon = new NotifyIcon
            {
                Text = "KageDesk \u2014 Ninja Desktop HQ",
                ContextMenuStrip = _contextMenu,
                Visible = true
            };

            CreateProceduralIcon();
            _notifyIcon.DoubleClick += (_, _) => WindowFocuser.TryFocusOrca();
            _contextMenu.Opening += (_, _) => RebuildMenu();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void CreateProceduralIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);

            // Dark mask shape
            using var maskBrush = new SolidBrush(Color.FromArgb(24, 28, 42));
            g.FillRectangle(maskBrush, 2, 2, 12, 12);

            // Headband
            using var bandBrush = new SolidBrush(Color.FromArgb(99, 102, 241));
            g.FillRectangle(bandBrush, 2, 4, 12, 3);

            // Metal plate
            using var plateBrush = new SolidBrush(Color.FromArgb(209, 213, 219));
            g.FillRectangle(plateBrush, 5, 4, 6, 3);

            // Glowing eyes
            using var eyeBrush = new SolidBrush(Color.FromArgb(34, 211, 238));
            g.FillRectangle(eyeBrush, 4, 8, 2, 2);
            g.FillRectangle(eyeBrush, 10, 8, 2, 2);
        }

        CleanupIconHandles();

        _hIcon = bmp.GetHicon();
        _managedIcon = Icon.FromHandle(_hIcon);
        if (_notifyIcon != null)
        {
            _notifyIcon.Icon = _managedIcon;
        }
    }

    private void CleanupIconHandles()
    {
        if (_managedIcon != null)
        {
            _managedIcon.Dispose();
            _managedIcon = null;
        }
        if (_hIcon != IntPtr.Zero)
        {
            Win32Interop.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    public void RebuildMenu()
    {
        if (_contextMenu == null) return;
        DisposeItemsRecursively(_contextMenu.Items);

        var config = GetConfig?.Invoke() ?? new AppConfig();
        var state = GetCurrentState?.Invoke();
        var currentTheme = GetCurrentTheme?.Invoke();

        // 1. Diagnostics Header
        var proc = Process.GetCurrentProcess();
        double wsMb = proc.WorkingSet64 / 1024.0 / 1024.0;
        int agentCount = state?.Agents.Count ?? 0;
        int workingCount = state?.Agents.Count(a => a.State == AgentState.Working) ?? 0;
        string diagText = $"KageDesk  \u2022  {wsMb:F1} MB  \u2022  {agentCount} Agents ({workingCount} Active)";
        var diagItem = new ToolStripMenuItem(diagText) { Enabled = false };
        _contextMenu.Items.Add(diagItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // 2. Wallpaper Status & Toggle
        bool isWpAttached = IsWallpaperAttached?.Invoke() ?? false;
        string wpStatus = GetWallpaperStatus?.Invoke() ?? (config.WallpaperEnabled ? "Enabled" : "Disabled");
        string wpLabel = $"Live Wallpaper ({wpStatus})";

        var wpItem = new ToolStripMenuItem(wpLabel, null, (_, _) =>
        {
            bool next = !config.WallpaperEnabled;
            UpdateConfig?.Invoke(c => c.WallpaperEnabled = next);
            OnWallpaperToggled?.Invoke(next);
        })
        {
            Checked = config.WallpaperEnabled
        };
        _contextMenu.Items.Add(wpItem);

        // 3. Pet Overlay Submenu
        var petMenu = new ToolStripMenuItem("Taskbar Pets");
        var chillItem = new ToolStripMenuItem("Mode: Always Chill", null, (_, _) =>
        {
            UpdateConfig?.Invoke(c => c.PetIdleMode = "chill");
            OnPetModeChanged?.Invoke("chill");
        }) { Checked = config.PetIdleMode == "chill" };

        var autoHideItem = new ToolStripMenuItem("Mode: Auto-Hide on Idle", null, (_, _) =>
        {
            UpdateConfig?.Invoke(c => c.PetIdleMode = "auto-hide");
            OnPetModeChanged?.Invoke("auto-hide");
        }) { Checked = config.PetIdleMode == "auto-hide" };

        var offItem = new ToolStripMenuItem("Mode: Off", null, (_, _) =>
        {
            UpdateConfig?.Invoke(c => c.PetIdleMode = "off");
            OnPetModeChanged?.Invoke("off");
        }) { Checked = config.PetIdleMode == "off" };

        petMenu.DropDownItems.Add(chillItem);
        petMenu.DropDownItems.Add(autoHideItem);
        petMenu.DropDownItems.Add(offItem);
        petMenu.DropDownItems.Add(new ToolStripSeparator());

        var scale1x = new ToolStripMenuItem("Scale: 1x (Retro 384px)", null, (_, _) =>
        {
            OnPetScaleChanged?.Invoke(1);
        }) { Checked = config.PetScale == 1 };

        var scale2x = new ToolStripMenuItem("Scale: 2x (HiDPI 768px)", null, (_, _) =>
        {
            OnPetScaleChanged?.Invoke(2);
        }) { Checked = config.PetScale == 2 };

        petMenu.DropDownItems.Add(scale1x);
        petMenu.DropDownItems.Add(scale2x);
        petMenu.DropDownItems.Add(new ToolStripSeparator());

        var resetPosItem = new ToolStripMenuItem("Reset Position to Right", null, (_, _) =>
        {
            UpdateConfig?.Invoke(c => c.PetX = null);
            OnResetPetPosition?.Invoke();
        });
        petMenu.DropDownItems.Add(resetPosItem);

        _contextMenu.Items.Add(petMenu);

        // 4. Themes Submenu
        var themeMenu = new ToolStripMenuItem("Theme Packs");
        var loadedThemes = GetLoadedThemes?.Invoke() ?? Array.Empty<ThemePack>();
        foreach (var th in loadedThemes)
        {
            var item = new ToolStripMenuItem(th.DisplayName, null, (_, _) =>
            {
                UpdateConfig?.Invoke(c => c.ActiveThemeId = th.Id);
                OnThemeChanged?.Invoke(th.Id);
            })
            {
                Checked = currentTheme?.Id == th.Id
            };
            themeMenu.DropDownItems.Add(item);
        }

        themeMenu.DropDownItems.Add(new ToolStripSeparator());
        themeMenu.DropDownItems.Add(new ToolStripMenuItem("Reload Themes", null, (_, _) => OnReloadThemes?.Invoke()));
        themeMenu.DropDownItems.Add(new ToolStripMenuItem("Open Themes Folder", null, (_, _) =>
        {
            string themesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "themes");
            if (Directory.Exists(themesPath))
            {
                Process.Start("explorer.exe", themesPath);
            }
        }));

        _contextMenu.Items.Add(themeMenu);

        // 5. Weather Submenu
        var weatherMenu = new ToolStripMenuItem("Weather");
        foreach (WeatherMode wm in Enum.GetValues<WeatherMode>())
        {
            var item = new ToolStripMenuItem(wm.ToString(), null, (_, _) =>
            {
                UpdateConfig?.Invoke(c => c.WeatherMode = wm);
                OnWeatherChanged?.Invoke(wm);
            })
            {
                Checked = config.WeatherMode == wm
            };
            weatherMenu.DropDownItems.Add(item);
        }
        _contextMenu.Items.Add(weatherMenu);

        // 6. Demo Studio Submenu (Canonical names routed directly to SetDemoScenario)
        var demoMenu = new ToolStripMenuItem("Demo Studio");
        demoMenu.DropDownItems.Add(new ToolStripMenuItem("Trigger Working Agents", null, (_, _) => OnDemoTrigger?.Invoke("working")));
        demoMenu.DropDownItems.Add(new ToolStripMenuItem("Trigger Needs Approval", null, (_, _) => OnDemoTrigger?.Invoke("approve")));
        demoMenu.DropDownItems.Add(new ToolStripMenuItem("Trigger Done (Celebration)", null, (_, _) => OnDemoTrigger?.Invoke("done")));
        demoMenu.DropDownItems.Add(new ToolStripMenuItem("Trigger Failure Alert", null, (_, _) => OnDemoTrigger?.Invoke("fail")));
        demoMenu.DropDownItems.Add(new ToolStripMenuItem("Trigger 4 Clones", null, (_, _) => OnDemoTrigger?.Invoke("clone")));
        demoMenu.DropDownItems.Add(new ToolStripSeparator());
        demoMenu.DropDownItems.Add(new ToolStripMenuItem("Reset to Live Orca", null, (_, _) => OnDemoTrigger?.Invoke("clear")));
        _contextMenu.Items.Add(demoMenu);

        // 7. Settings Submenu
        var settingsMenu = new ToolStripMenuItem("Settings");
        var autoPauseItem = new ToolStripMenuItem("Auto-Pause Wallpaper When Covered", null, (_, _) =>
        {
            bool next = !config.AutoPauseWallpaperWhenCovered;
            UpdateConfig?.Invoke(c => c.AutoPauseWallpaperWhenCovered = next);
            OnAutoPauseToggled?.Invoke(next);
        }) { Checked = config.AutoPauseWallpaperWhenCovered };

        var gameDndItem = new ToolStripMenuItem("Hide Pets in Fullscreen Game (DND)", null, (_, _) =>
        {
            bool next = !config.HidePetsInFullscreenGame;
            UpdateConfig?.Invoke(c => c.HidePetsInFullscreenGame = next);
            OnGameDndToggled?.Invoke(next);
        }) { Checked = config.HidePetsInFullscreenGame };

        var procAgentItem = new ToolStripMenuItem("Process Agent Collector", null, (_, _) =>
        {
            bool next = !config.EnableProcessAgentCollector;
            UpdateConfig?.Invoke(c => c.EnableProcessAgentCollector = next);
            OnProcessAgentCollectorToggled?.Invoke(next);
        }) { Checked = config.EnableProcessAgentCollector };

        var ghCiItem = new ToolStripMenuItem("GitHub CI Collector", null, (_, _) =>
        {
            bool next = !config.EnableGitHubCiCollector;
            UpdateConfig?.Invoke(c => c.EnableGitHubCiCollector = next);
            OnGitHubCiCollectorToggled?.Invoke(next);
        }) { Checked = config.EnableGitHubCiCollector };

        var startupItem = new ToolStripMenuItem("Launch on Windows Startup", null, (_, _) =>
        {
            bool target = !StartupManager.IsStartupEnabled();
            if (StartupManager.SetStartup(target))
            {
                UpdateConfig?.Invoke(c => c.LaunchOnStartup = target);
            }
        }) { Checked = StartupManager.IsStartupEnabled() };

        settingsMenu.DropDownItems.Add(autoPauseItem);
        settingsMenu.DropDownItems.Add(gameDndItem);
        settingsMenu.DropDownItems.Add(new ToolStripSeparator());
        settingsMenu.DropDownItems.Add(procAgentItem);
        settingsMenu.DropDownItems.Add(ghCiItem);
        settingsMenu.DropDownItems.Add(new ToolStripSeparator());
        settingsMenu.DropDownItems.Add(startupItem);

        _contextMenu.Items.Add(settingsMenu);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // 8. Focus Orca
        _contextMenu.Items.Add(new ToolStripMenuItem("Focus Orca", null, (_, _) => WindowFocuser.TryFocusOrca()));

        // 9. Exit
        _contextMenu.Items.Add(new ToolStripMenuItem("Exit KageDesk", null, (_, _) => _onExit()));
    }

    private static void DisposeItemsRecursively(ToolStripItemCollection items)
    {
        for (int i = items.Count - 1; i >= 0; i--)
        {
            ToolStripItem item = items[i];
            if (item is ToolStripDropDownItem dropDown)
            {
                DisposeItemsRecursively(dropDown.DropDownItems);
            }
            item.Dispose();
        }
        items.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        if (_contextMenu != null)
        {
            DisposeItemsRecursively(_contextMenu.Items);
            _contextMenu.Dispose();
            _contextMenu = null;
        }

        CleanupIconHandles();
    }
}
