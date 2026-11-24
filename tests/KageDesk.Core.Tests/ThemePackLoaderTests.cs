using System.Text.Json;
using KageDesk.Core.Models;
using KageDesk.Core.Themes;
using Xunit;

namespace KageDesk.Core.Tests;

public class ThemePackLoaderTests
{
    [Fact]
    public void LoadThemes_MissingDirectory_ReturnsBuiltInFallbackWithWarning()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), "kagedesk_nonexistent_" + Guid.NewGuid().ToString("N"));
        var result = ThemePackLoader.LoadThemes(nonExistentPath);

        Assert.NotEmpty(result.Themes);
        Assert.Equal("shinobi-hq", result.FallbackTheme.Id);
        Assert.Contains(result.Themes, t => t.Id == "shinobi-hq");
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void LoadThemes_ValidThemeFolder_LoadsThemeSuccessfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "kagedesk_themes_" + Guid.NewGuid().ToString("N"));
        var customThemeDir = Path.Combine(tempDir, "leaf-village");
        Directory.CreateDirectory(customThemeDir);

        try
        {
            var validThemeJson = """
            {
              "id": "leaf-village",
              "displayName": "Hidden Leaf Village",
              "version": "1.0.0",
              "author": "Konoha",
              "palette": {
                "background": "#0f2027",
                "surface": "#203a43",
                "border": "#2c5364",
                "accent": "#43cea2",
                "text": "#ffffff"
              },
              "weather": {
                "dawn": "#ffd89b",
                "day": "#19547b",
                "sunset": "#f857a6",
                "midnightRain": "#232526",
                "stormAlert": "#e53935"
              },
              "layout": {
                "maxSlots": 6,
                "deskSpacing": 20,
                "slotWidth": 72,
                "slotHeight": 72
              },
              "characters": {}
            }
            """;

            File.WriteAllText(Path.Combine(customThemeDir, "theme.json"), validThemeJson);

            var result = ThemePackLoader.LoadThemes(tempDir);
            Assert.Contains(result.Themes, t => t.Id == "leaf-village");
            var loaded = result.Themes.First(t => t.Id == "leaf-village");
            Assert.Equal("Hidden Leaf Village", loaded.DisplayName);
            Assert.Equal("#0f2027", loaded.Palette.Background);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void ParseAndValidateTheme_MissingRequiredFields_ReturnsNullAndWarning()
    {
        var missingIdJson = """
        {
          "displayName": "Theme Without ID",
          "palette": {
            "background": "#111111", "surface": "#222222", "border": "#333333", "accent": "#444444", "text": "#555555"
          },
          "weather": {
            "dawn": "#111111", "day": "#222222", "sunset": "#333333", "midnightRain": "#444444", "stormAlert": "#555555"
          }
        }
        """;

        var (theme, warnings) = ThemePackLoader.ParseAndValidateTheme(missingIdJson, "test_missing_id");
        Assert.Null(theme);
        Assert.Contains(warnings, w => w.Contains("missing required 'id'"));
    }

    [Fact]
    public void ParseAndValidateTheme_InvalidHexColor_ReturnsNullAndWarning()
    {
        var invalidHexJson = """
        {
          "id": "bad-color",
          "displayName": "Bad Color Theme",
          "palette": {
            "background": "red",
            "surface": "#222222",
            "border": "#333333",
            "accent": "#444444",
            "text": "#555555"
          },
          "weather": {
            "dawn": "#111111", "day": "#222222", "sunset": "#333333", "midnightRain": "#444444", "stormAlert": "#555555"
          }
        }
        """;

        var (theme, warnings) = ThemePackLoader.ParseAndValidateTheme(invalidHexJson, "test_invalid_hex");
        Assert.Null(theme);
        Assert.Contains(warnings, w => w.Contains("invalid hex color"));
    }

    [Theory]
    [InlineData("#1a1b26", true)]
    [InlineData("#FF1A1B26", true)]
    [InlineData("#ffffff", true)]
    [InlineData("#123456", true)]
    [InlineData("1a1b26", false)] // missing #
    [InlineData("#12345", false)] // too short
    [InlineData("#1234567", false)] // 7 chars
    [InlineData("#zz1122", false)] // invalid hex chars
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidHexColor_ValidatesCorrectly(string? hex, bool expected)
    {
        Assert.Equal(expected, ThemePackLoader.IsValidHexColor(hex));
    }

    [Fact]
    public void ParseAndValidateTheme_ValidSceneColors_ParsesWithZeroWarnings()
    {
        var json = """
        {
          "id": "custom-scene",
          "displayName": "Custom Scene",
          "version": "1.0.0",
          "author": "Test",
          "palette": {
            "background": "#111111", "surface": "#222222", "border": "#333333", "accent": "#444444", "text": "#555555"
          },
          "weather": {
            "dawn": "#111111", "day": "#222222", "sunset": "#333333", "midnightRain": "#444444", "stormAlert": "#555555"
          },
          "scene": {
            "wallTop": "#171b28",
            "wallBottom": "#121622",
            "floorBase": "#2b1f17",
            "skylineStyle": "cyber"
          }
        }
        """;

        var (theme, warnings) = ThemePackLoader.ParseAndValidateTheme(json, "test_valid_scene");
        Assert.NotNull(theme);
        Assert.Empty(warnings);
        Assert.Equal("#171b28", theme!.Scene?.WallTop);
        Assert.Equal("cyber", theme.Scene?.SkylineStyle);
    }

    [Fact]
    public void ParseAndValidateTheme_InvalidSceneHex_ReturnsNullAndWarning()
    {
        var json = """
        {
          "id": "bad-scene",
          "displayName": "Bad Scene",
          "palette": {
            "background": "#111111", "surface": "#222222", "border": "#333333", "accent": "#444444", "text": "#555555"
          },
          "weather": {
            "dawn": "#111111", "day": "#222222", "sunset": "#333333", "midnightRain": "#444444", "stormAlert": "#555555"
          },
          "scene": {
            "wallTop": "not-a-hex"
          }
        }
        """;

        var (theme, warnings) = ThemePackLoader.ParseAndValidateTheme(json, "test_bad_scene");
        Assert.Null(theme);
        Assert.Contains(warnings, w => w.Contains("invalid hex color for scene"));
    }
}
