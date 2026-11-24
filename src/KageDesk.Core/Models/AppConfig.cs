namespace KageDesk.Core.Models;

public class AppConfig
{
    public int PollingIntervalMs { get; set; } = 2500;
    public int LocalBridgePort { get; set; } = 4319;
    public WeatherMode WeatherMode { get; set; } = WeatherMode.Auto;
    public string ActiveThemeId { get; set; } = "shinobi-hq";
    public bool EnableProcessAgentCollector { get; set; } = false;
    public bool EnableGitHubCiCollector { get; set; } = false;
    public List<string> GitHubRepos { get; set; } = new();
    public double CelebrationDurationSeconds { get; set; } = 6.0;
}
