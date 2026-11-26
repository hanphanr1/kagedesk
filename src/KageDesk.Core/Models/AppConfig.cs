namespace KageDesk.Core.Models;

public class AppConfig
{
    public int ConfigVersion { get; set; } = 2;
    public int PollingIntervalMs { get; set; } = 2500;
    public int LocalBridgePort { get; set; } = 4319;
    public WeatherMode WeatherMode { get; set; } = WeatherMode.Auto;
    public string ActiveThemeId { get; set; } = "shinobi-hq";
    public bool EnableProcessAgentCollector { get; set; } = true;
    public bool EnableGitHubCiCollector { get; set; } = false;
    public List<string> GitHubRepos { get; set; } = new();
    public double CelebrationDurationSeconds { get; set; } = 6.0;

    public bool WallpaperEnabled { get; set; } = true;
    public bool PetOverlayEnabled { get; set; } = true;
    public string PetIdleMode { get; set; } = "chill";
    public int PetScale { get; set; } = 2;
    public int? PetX { get; set; } = null;
    public bool AutoPauseWallpaperWhenCovered { get; set; } = true;
    public bool HidePetsInFullscreenGame { get; set; } = true;
    public int TargetFps { get; set; } = 15;
    public bool LaunchOnStartup { get; set; } = false;
    public bool SoundEnabled { get; set; } = true;
    public bool ShowMixiWhenAllIdle { get; set; } = true;
    public string? PinnedMixiActivity { get; set; } = null;
    public string WallpaperMonitorDeviceName { get; set; } = "";
    public string PetMonitorDeviceName { get; set; } = "";
    public int? PetRelativeX { get; set; } = null;
    public bool EnableMediaSync { get; set; } = true;
    public bool EnableSessionLogWatcher { get; set; } = true;
}
