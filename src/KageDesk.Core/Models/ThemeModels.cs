namespace KageDesk.Core.Models;

public record ThemePalette(
    string Background,
    string Surface,
    string Border,
    string Accent,
    string Text
);

public record WeatherPalette(
    string Dawn,
    string Day,
    string Sunset,
    string MidnightRain,
    string StormAlert
);

public record DeskLayout(
    int MaxSlots = 8,
    int DeskSpacing = 16,
    int SlotWidth = 64,
    int SlotHeight = 64
);

public record CharacterStyle(
    string Sprite,
    string? IdleAnimation = null,
    string? WorkAnimation = null,
    string? AlertAnimation = null
);

public record SceneColors(
    string? WallTop = null,
    string? WallBottom = null,
    string? FloorBase = null,
    string? FloorLine = null,
    string? RugOuter = null,
    string? RugInner = null,
    string? DeskTop = null,
    string? DeskFront = null,
    string? WindowFrame = null,
    string? BoardBg = null,
    string? SkylineStyle = null
);

public record ThemePack(
    string Id,
    string DisplayName,
    string Version,
    string Author,
    ThemePalette Palette,
    WeatherPalette Weather,
    DeskLayout Layout,
    IReadOnlyDictionary<string, CharacterStyle> Characters,
    SceneColors? Scene = null
);
