using System.Text.Json;
using System.Text.RegularExpressions;
using KageDesk.Core.Models;

namespace KageDesk.Core.Themes;

public record ThemeLoadResult(
    IReadOnlyList<ThemePack> Themes,
    IReadOnlyList<string> Warnings,
    ThemePack FallbackTheme
);

public static class ThemePackLoader
{
    private static readonly Regex HexColorRegex = new(@"^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static ThemePack BuiltInFallback => CreateDefaultShinobiTheme();

    public static ThemeLoadResult LoadThemes(string themesDirectory)
    {
        var warnings = new List<string>();
        var themes = new List<ThemePack>();

        if (!Directory.Exists(themesDirectory))
        {
            warnings.Add($"Themes directory '{themesDirectory}' does not exist. Using built-in fallback theme.");
            themes.Add(BuiltInFallback);
            return new ThemeLoadResult(themes, warnings, BuiltInFallback);
        }

        try
        {
            var directories = Directory.GetDirectories(themesDirectory);
            foreach (var dir in directories)
            {
                var themeJsonPath = Path.Combine(dir, "theme.json");
                if (!File.Exists(themeJsonPath))
                {
                    continue; // Skip folders without theme.json
                }

                try
                {
                    var json = File.ReadAllText(themeJsonPath);
                    var (theme, themeWarnings) = ParseAndValidateTheme(json, dir);
                    if (theme != null)
                    {
                        themes.Add(theme);
                    }
                    warnings.AddRange(themeWarnings);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Failed to read theme at '{themeJsonPath}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to enumerate themes directory '{themesDirectory}': {ex.Message}");
        }

        // Guarantee fallback is present if list is empty or if fallback wasn't loaded from disk
        if (!themes.Any(t => string.Equals(t.Id, BuiltInFallback.Id, StringComparison.OrdinalIgnoreCase)))
        {
            themes.Insert(0, BuiltInFallback);
        }

        return new ThemeLoadResult(themes, warnings, BuiltInFallback);
    }

    public static (ThemePack? Theme, IReadOnlyList<string> Warnings) ParseAndValidateTheme(string json, string sourceContext)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            warnings.Add($"Theme JSON is empty in '{sourceContext}'.");
            return (null, warnings);
        }

        ThemePack? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ThemePack>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            warnings.Add($"Malformed theme JSON in '{sourceContext}': {ex.Message}");
            return (null, warnings);
        }

        if (parsed == null)
        {
            warnings.Add($"Null theme payload in '{sourceContext}'.");
            return (null, warnings);
        }

        // 1. Required fields
        if (string.IsNullOrWhiteSpace(parsed.Id))
        {
            warnings.Add($"Theme in '{sourceContext}' missing required 'id'.");
            return (null, warnings);
        }

        if (string.IsNullOrWhiteSpace(parsed.DisplayName))
        {
            warnings.Add($"Theme '{parsed.Id}' missing required 'displayName'.");
            return (null, warnings);
        }

        // 2. Palette validation
        if (parsed.Palette == null)
        {
            warnings.Add($"Theme '{parsed.Id}' missing 'palette'.");
            return (null, warnings);
        }

        var paletteColors = new (string Name, string? Value)[]
        {
            (nameof(parsed.Palette.Background), parsed.Palette.Background),
            (nameof(parsed.Palette.Surface), parsed.Palette.Surface),
            (nameof(parsed.Palette.Border), parsed.Palette.Border),
            (nameof(parsed.Palette.Accent), parsed.Palette.Accent),
            (nameof(parsed.Palette.Text), parsed.Palette.Text)
        };

        foreach (var (cName, cVal) in paletteColors)
        {
            if (string.IsNullOrWhiteSpace(cVal) || !IsValidHexColor(cVal))
            {
                warnings.Add($"Theme '{parsed.Id}' has invalid hex color for palette '{cName}': '{cVal}'. Must be #RRGGBB or #AARRGGBB.");
                return (null, warnings);
            }
        }

        // 3. Weather palette validation
        if (parsed.Weather == null)
        {
            warnings.Add($"Theme '{parsed.Id}' missing 'weather' palette.");
            return (null, warnings);
        }

        var weatherColors = new (string Name, string? Value)[]
        {
            (nameof(parsed.Weather.Dawn), parsed.Weather.Dawn),
            (nameof(parsed.Weather.Day), parsed.Weather.Day),
            (nameof(parsed.Weather.Sunset), parsed.Weather.Sunset),
            (nameof(parsed.Weather.MidnightRain), parsed.Weather.MidnightRain),
            (nameof(parsed.Weather.StormAlert), parsed.Weather.StormAlert)
        };

        foreach (var (wName, wVal) in weatherColors)
        {
            if (string.IsNullOrWhiteSpace(wVal) || !IsValidHexColor(wVal))
            {
                warnings.Add($"Theme '{parsed.Id}' has invalid hex color for weather '{wName}': '{wVal}'. Must be #RRGGBB or #AARRGGBB.");
                return (null, warnings);
            }
        }

        // 4. Optional scene palette validation
        if (parsed.Scene != null)
        {
            var sceneColors = new (string Name, string? Value)[]
            {
                (nameof(parsed.Scene.WallTop), parsed.Scene.WallTop),
                (nameof(parsed.Scene.WallBottom), parsed.Scene.WallBottom),
                (nameof(parsed.Scene.FloorBase), parsed.Scene.FloorBase),
                (nameof(parsed.Scene.FloorLine), parsed.Scene.FloorLine),
                (nameof(parsed.Scene.RugOuter), parsed.Scene.RugOuter),
                (nameof(parsed.Scene.RugInner), parsed.Scene.RugInner),
                (nameof(parsed.Scene.DeskTop), parsed.Scene.DeskTop),
                (nameof(parsed.Scene.DeskFront), parsed.Scene.DeskFront),
                (nameof(parsed.Scene.WindowFrame), parsed.Scene.WindowFrame),
                (nameof(parsed.Scene.BoardBg), parsed.Scene.BoardBg)
            };

            foreach (var (sName, sVal) in sceneColors)
            {
                if (sVal != null && !IsValidHexColor(sVal))
                {
                    warnings.Add($"Theme '{parsed.Id}' has invalid hex color for scene '{sName}': '{sVal}'. Must be #RRGGBB or #AARRGGBB.");
                    return (null, warnings);
                }
            }

            if (parsed.Scene.SkylineStyle != null)
            {
                var style = parsed.Scene.SkylineStyle.Trim().ToLowerInvariant();
                if (style is not ("konoha" or "cyber" or "lofi"))
                {
                    warnings.Add($"Theme '{parsed.Id}' has invalid scene skylineStyle '{parsed.Scene.SkylineStyle}'. Expected 'konoha', 'cyber', or 'lofi'.");
                    return (null, warnings);
                }
            }
        }

        return (parsed, warnings);
    }

    public static bool IsValidHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return false;
        return HexColorRegex.IsMatch(hex.Trim());
    }

    public static ThemePack CreateDefaultShinobiTheme() => new(
        Id: "shinobi-hq",
        DisplayName: "Shinobi HQ",
        Version: "1.0.0",
        Author: "KageDesk",
        Palette: new ThemePalette(
            Background: "#121622",
            Surface: "#1b2234",
            Border: "#4c3d32",
            Accent: "#f59e0b",
            Text: "#f3ead8"
        ),
        Weather: new WeatherPalette(
            Dawn: "#f6b879",
            Day: "#60a5fa",
            Sunset: "#f97316",
            MidnightRain: "#1e293b",
            StormAlert: "#991b1b"
        ),
        Layout: new DeskLayout(
            MaxSlots: 8,
            DeskSpacing: 16,
            SlotWidth: 64,
            SlotHeight: 64
        ),
        Characters: new Dictionary<string, CharacterStyle>
        {
            ["shikamaru"] = new("shikamaru.png", "idle", "think", "alert"),
            ["minato"] = new("minato.png", "idle", "flash", "alert"),
            ["tsunade"] = new("tsunade.png", "idle", "heal", "alert"),
            ["kakashi"] = new("kakashi.png", "idle", "read", "alert"),
            ["clone"] = new("clone.png", "idle", "work", "smoke"),
            ["generic"] = new("generic.png", "idle", "work", "alert")
        },
        Scene: new SceneColors(
            WallTop: "#171b28",
            WallBottom: "#121622",
            FloorBase: "#2b1f17",
            FloorLine: "#1e1510",
            RugOuter: "#4a1d2f",
            RugInner: "#642940",
            DeskTop: "#6b4f35",
            DeskFront: "#4b3523",
            WindowFrame: "#513c2a",
            BoardBg: "#1a2030",
            SkylineStyle: "konoha"
        )
    );
}
