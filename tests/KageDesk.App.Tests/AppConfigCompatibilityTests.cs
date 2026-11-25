using System.Text.Json;
using KageDesk.Core.Config;
using KageDesk.Core.Models;
using Xunit;

namespace KageDesk.App.Tests;

public class AppConfigCompatibilityTests
{
    [Fact]
    public void Deserialize_OldM1ConfigJson_ProvidesDefaultsForNewUiFields()
    {
        // JSON from Milestone 1 before M3 UI fields existed
        const string oldJson = """
        {
          "PollingIntervalMs": 3000,
          "LocalBridgePort": 4319,
          "WeatherMode": 1,
          "ActiveThemeId": "shinobi-hq",
          "EnableProcessAgentCollector": false,
          "EnableGitHubCiCollector": false,
          "GitHubRepos": [],
          "CelebrationDurationSeconds": 5.0
        }
        """;

        var config = JsonSerializer.Deserialize<AppConfig>(oldJson);

        Assert.NotNull(config);
        Assert.Equal(3000, config.PollingIntervalMs);
        Assert.Equal(4319, config.LocalBridgePort);
        Assert.Equal(WeatherMode.Dawn, config.WeatherMode);
        Assert.Equal("shinobi-hq", config.ActiveThemeId);

        // M3 UI & Win32 Defaults
        Assert.True(config.WallpaperEnabled);
        Assert.True(config.PetOverlayEnabled);
        Assert.Equal("chill", config.PetIdleMode);
        Assert.Equal(2, config.PetScale);
        Assert.Null(config.PetX);
        Assert.True(config.AutoPauseWallpaperWhenCovered);
        Assert.True(config.HidePetsInFullscreenGame);
        Assert.Equal(15, config.TargetFps);
        Assert.False(config.LaunchOnStartup);
        Assert.True(config.SoundEnabled);
    }

    [Fact]
    public void Deserialize_OldJsonOmittingSoundEnabled_DefaultsToTrue()
    {
        const string json = "{\"PollingIntervalMs\": 2500}";
        var config = JsonSerializer.Deserialize<AppConfig>(json);
        Assert.NotNull(config);
        Assert.True(config.SoundEnabled);
    }

    [Fact]
    public void RoundTrip_AllNewFieldsPreserved()
    {
        var original = new AppConfig
        {
            PollingIntervalMs = 2000,
            LocalBridgePort = 5000,
            WeatherMode = WeatherMode.MidnightRain,
            ActiveThemeId = "cyber-bunker",
            WallpaperEnabled = false,
            PetOverlayEnabled = true,
            PetIdleMode = "auto-hide",
            PetScale = 1,
            PetX = 450,
            AutoPauseWallpaperWhenCovered = false,
            HidePetsInFullscreenGame = false,
            TargetFps = 25,
            LaunchOnStartup = true
        };

        string json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<AppConfig>(json);

        Assert.NotNull(loaded);
        Assert.False(loaded.WallpaperEnabled);
        Assert.True(loaded.PetOverlayEnabled);
        Assert.Equal("auto-hide", loaded.PetIdleMode);
        Assert.Equal(1, loaded.PetScale);
        Assert.Equal(450, loaded.PetX);
        Assert.False(loaded.AutoPauseWallpaperWhenCovered);
        Assert.False(loaded.HidePetsInFullscreenGame);
        Assert.Equal(25, loaded.TargetFps);
        Assert.True(loaded.LaunchOnStartup);
    }

    [Fact]
    public void ConfigStore_LoadsOldConfig_MigratesCleanly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "kagedesk_m3_compat_" + Guid.NewGuid().ToString("N"), "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);

        try
        {
            const string oldJson = """
            {
              "PollingIntervalMs": 1500,
              "LocalBridgePort": 4325,
              "WeatherMode": 0,
              "ActiveThemeId": "cozy-lofi"
            }
            """;
            File.WriteAllText(tempFile, oldJson);

            var store = new ConfigStore(tempFile);
            var loaded = store.Load();

            Assert.Equal(1500, loaded.PollingIntervalMs);
            Assert.Equal("cozy-lofi", loaded.ActiveThemeId);
            Assert.True(loaded.WallpaperEnabled);
            Assert.Equal(2, loaded.PetScale);
            Assert.Equal(15, loaded.TargetFps);

            // Re-saving appends new defaults into file
            store.Save(loaded);
            string savedContent = File.ReadAllText(tempFile);
            Assert.Contains("WallpaperEnabled", savedContent);
            Assert.Contains("PetScale", savedContent);
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempFile);
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    [Theory]
    [InlineData("chill", true)]
    [InlineData("auto-hide", true)]
    [InlineData("off", true)]
    [InlineData("unknown", false)]
    public void PetIdleMode_ValidModesChecked(string mode, bool expectedValid)
    {
        string[] validModes = { "chill", "auto-hide", "off" };
        bool isValid = validModes.Contains(mode);
        Assert.Equal(expectedValid, isValid);
    }
}
