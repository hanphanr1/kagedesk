using KageDesk.Core.Themes;

namespace KageDesk.Rendering.Tests;

public class ThemePacksOnDiskTests
{
    internal static string FindThemesDirectory()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "themes");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "shinobi-hq", "theme.json")))
            {
                return candidate;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository 'themes' directory from test base directory.");
    }

    [Fact]
    public void LoadThemes_AllThreeDiskThemePacks_LoadWithZeroWarnings()
    {
        string themesDir = FindThemesDirectory();
        ThemeLoadResult result = ThemePackLoader.LoadThemes(themesDir);

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Themes.Count);

        var byId = result.Themes.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        Assert.True(byId.ContainsKey("shinobi-hq"));
        Assert.True(byId.ContainsKey("cyber-bunker"));
        Assert.True(byId.ContainsKey("cozy-lofi"));

        Assert.Equal("Shinobi HQ", byId["shinobi-hq"].DisplayName);
        Assert.Equal("konoha", byId["shinobi-hq"].Scene?.SkylineStyle);

        Assert.Equal("Cyber Bunker", byId["cyber-bunker"].DisplayName);
        Assert.Equal("cyber", byId["cyber-bunker"].Scene?.SkylineStyle);

        Assert.Equal("Cozy Lo-Fi", byId["cozy-lofi"].DisplayName);
        Assert.Equal("lofi", byId["cozy-lofi"].Scene?.SkylineStyle);
    }

    [Fact]
    public void DiskThemes_HaveDistinctScenePalettesAndAllSixCharacterEntries()
    {
        string themesDir = FindThemesDirectory();
        ThemeLoadResult result = ThemePackLoader.LoadThemes(themesDir);

        var wallTops = result.Themes.Select(t => t.Scene?.WallTop).Distinct().ToList();
        var floorBases = result.Themes.Select(t => t.Scene?.FloorBase).Distinct().ToList();
        var skylines = result.Themes.Select(t => t.Scene?.SkylineStyle).Distinct().ToList();

        Assert.Equal(3, wallTops.Count);
        Assert.Equal(3, floorBases.Count);
        Assert.Equal(3, skylines.Count);

        string[] expectedChars = { "shikamaru", "minato", "tsunade", "kakashi", "clone", "generic" };
        foreach (var theme in result.Themes)
        {
            Assert.NotNull(theme.Scene);
            foreach (string key in expectedChars)
            {
                Assert.True(theme.Characters.ContainsKey(key), $"Theme '{theme.Id}' missing character '{key}'.");
            }
        }
    }
}
