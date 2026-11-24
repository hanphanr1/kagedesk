using System.Text.Json;
using System.Text.Json.Serialization;
using KageDesk.Core.Models;

namespace KageDesk.Core.Config;

public class ConfigStore
{
    private readonly string _configFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ConfigFilePath => _configFilePath;

    public ConfigStore() : this(GetDefaultConfigPath())
    {
    }

    public ConfigStore(string configFilePath)
    {
        _configFilePath = configFilePath ?? throw new ArgumentNullException(nameof(configFilePath));
    }

    public static string GetDefaultConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "KageDesk", "config.json");
    }

    public AppConfig Load()
    {
        if (!File.Exists(_configFilePath))
        {
            var defaults = new AppConfig();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_configFilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            return config ?? new AppConfig();
        }
        catch (Exception)
        {
            // Back up corrupt file and restore defaults
            try
            {
                var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
                var backupPath = $"{_configFilePath}.corrupt.{timestamp}.bak";
                File.Copy(_configFilePath, backupPath, overwrite: true);
            }
            catch { }

            var defaults = new AppConfig();
            Save(defaults);
            return defaults;
        }
    }

    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var dir = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmpPath = $"{_configFilePath}.tmp";
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, _configFilePath, overwrite: true);
    }
}
