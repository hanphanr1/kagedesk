using KageDesk.Core.Config;
using KageDesk.Core.Models;
using Xunit;

namespace KageDesk.Core.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Load_FileDoesNotExist_ReturnsDefaultsAndCreatesFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "kagedesk_cfg_" + Guid.NewGuid().ToString("N"), "config.json");

        try
        {
            var store = new ConfigStore(tempFile);
            var config = store.Load();

            Assert.NotNull(config);
            Assert.Equal(2500, config.PollingIntervalMs);
            Assert.Equal(4319, config.LocalBridgePort);
            Assert.Equal(WeatherMode.Auto, config.WeatherMode);
            Assert.Equal("shinobi-hq", config.ActiveThemeId);
            Assert.True(config.ShowMixiWhenAllIdle);
            Assert.True(File.Exists(tempFile));
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

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesValues()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "kagedesk_cfg_" + Guid.NewGuid().ToString("N"), "config.json");

        try
        {
            var store = new ConfigStore(tempFile);
            var customConfig = new AppConfig
            {
                PollingIntervalMs = 1234,
                LocalBridgePort = 5432,
                WeatherMode = WeatherMode.MidnightRain,
                ActiveThemeId = "leaf-village",
                EnableProcessAgentCollector = true,
                EnableGitHubCiCollector = true,
                GitHubRepos = new List<string> { "hanphanr1/kagedesk" },
                CelebrationDurationSeconds = 10.0,
                ShowMixiWhenAllIdle = false
            };

            store.Save(customConfig);

            // Load with a fresh store instance
            var store2 = new ConfigStore(tempFile);
            var loaded = store2.Load();

            Assert.Equal(1234, loaded.PollingIntervalMs);
            Assert.Equal(5432, loaded.LocalBridgePort);
            Assert.Equal(WeatherMode.MidnightRain, loaded.WeatherMode);
            Assert.Equal("leaf-village", loaded.ActiveThemeId);
            Assert.True(loaded.EnableProcessAgentCollector);
            Assert.True(loaded.EnableGitHubCiCollector);
            Assert.Single(loaded.GitHubRepos);
            Assert.Equal("hanphanr1/kagedesk", loaded.GitHubRepos[0]);
            Assert.Equal(10.0, loaded.CelebrationDurationSeconds);
            Assert.False(loaded.ShowMixiWhenAllIdle);
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

    [Fact]
    public void Load_CorruptJson_BacksUpFileAndRestoresDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kagedesk_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tempFile = Path.Combine(dir, "config.json");

        try
        {
            // Write corrupt JSON
            File.WriteAllText(tempFile, "MALFORMED_JSON_CONTENT{{{");

            var store = new ConfigStore(tempFile);
            var loaded = store.Load();

            // Must restore defaults without throwing
            Assert.NotNull(loaded);
            Assert.Equal(2500, loaded.PollingIntervalMs);
            Assert.Equal(4319, loaded.LocalBridgePort);

            // Verify corrupt backup was created
            var backupFiles = Directory.GetFiles(dir, "config.json.corrupt.*.bak");
            Assert.Single(backupFiles);
            Assert.Equal("MALFORMED_JSON_CONTENT{{{", File.ReadAllText(backupFiles[0]));

            // Config file should now contain valid JSON
            var roundTrip = store.Load();
            Assert.Equal(2500, roundTrip.PollingIntervalMs);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void Load_V1Config_MigratesEnableProcessAgentCollectorAndSavesV2()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kagedesk_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tempFile = Path.Combine(dir, "config.json");

        try
        {
            // Simulate v1 config with ConfigVersion absent (0) and EnableProcessAgentCollector = false
            string v1Json = "{\n  \"PollingIntervalMs\": 2500,\n  \"EnableProcessAgentCollector\": false\n}";
            File.WriteAllText(tempFile, v1Json);

            var store = new ConfigStore(tempFile);
            var loaded = store.Load();

            Assert.Equal(2, loaded.ConfigVersion);
            Assert.True(loaded.EnableProcessAgentCollector);

            // Verify file on disk was updated atomically with version 2
            string savedJson = File.ReadAllText(tempFile);
            Assert.Contains("\"ConfigVersion\": 2", savedJson);
            Assert.Contains("\"EnableProcessAgentCollector\": true", savedJson);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void Load_V2ConfigWithExplicitFalse_PreservesFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kagedesk_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tempFile = Path.Combine(dir, "config.json");

        try
        {
            // Explicit v2 opt-out
            string v2Json = "{\n  \"ConfigVersion\": 2,\n  \"EnableProcessAgentCollector\": false\n}";
            File.WriteAllText(tempFile, v2Json);

            var store = new ConfigStore(tempFile);
            var loaded = store.Load();

            Assert.Equal(2, loaded.ConfigVersion);
            Assert.False(loaded.EnableProcessAgentCollector);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void Load_V2ConfigWithCamelCaseConfigVersion_PreservesFalseAndDoesNotRewrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kagedesk_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tempFile = Path.Combine(dir, "config.json");

        try
        {
            string camelCaseJson = "{\n  \"configVersion\": 2,\n  \"enableProcessAgentCollector\": false\n}";
            File.WriteAllText(tempFile, camelCaseJson);
            var initialWriteTime = File.GetLastWriteTimeUtc(tempFile);

            var store = new ConfigStore(tempFile);
            var loaded = store.Load();

            Assert.Equal(2, loaded.ConfigVersion);
            Assert.False(loaded.EnableProcessAgentCollector);

            // Verify file content was not overwritten/flipped to true
            string currentContent = File.ReadAllText(tempFile);
            Assert.Contains("\"enableProcessAgentCollector\": false", currentContent);
            Assert.Equal(camelCaseJson, currentContent);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void Load_DuplicateConfigVersionProperties_LastWinsWithoutRewriting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kagedesk_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tempFile = Path.Combine(dir, "config.json");

        try
        {
            string duplicateJson = "{\"ConfigVersion\":1,\"configVersion\":2,\"enableProcessAgentCollector\":false}";
            File.WriteAllText(tempFile, duplicateJson);

            var store = new ConfigStore(tempFile);
            var loaded = store.Load();

            Assert.Equal(2, loaded.ConfigVersion);
            Assert.False(loaded.EnableProcessAgentCollector);

            // Verify file was NOT rewritten/migrated
            string contentOnDisk = File.ReadAllText(tempFile);
            Assert.Equal(duplicateJson, contentOnDisk);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void Load_DuplicateConfigVersionProperties_LastWinsV1_MigratesAndRewrites()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kagedesk_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tempFile = Path.Combine(dir, "config.json");

        try
        {
            string duplicateJson = "{\"ConfigVersion\":2,\"configVersion\":1,\"enableProcessAgentCollector\":false}";
            File.WriteAllText(tempFile, duplicateJson);

            var store = new ConfigStore(tempFile);
            var loaded = store.Load();

            Assert.Equal(2, loaded.ConfigVersion);
            Assert.True(loaded.EnableProcessAgentCollector);

            // Verify file was rewritten with v2
            string contentOnDisk = File.ReadAllText(tempFile);
            Assert.Contains("\"ConfigVersion\": 2", contentOnDisk);
            Assert.Contains("\"EnableProcessAgentCollector\": true", contentOnDisk);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}

