using KageDesk.Core.Models;
using KageDesk.Rendering.Canvas;

namespace KageDesk.Rendering.Scenes;

internal readonly struct ResolvedScenePalette
{
    public readonly int Background;
    public readonly int Surface;
    public readonly int Border;
    public readonly int Accent;
    public readonly int Text;

    public readonly int WallTop;
    public readonly int WallBottom;
    public readonly int FloorBase;
    public readonly int FloorLine;
    public readonly int RugOuter;
    public readonly int RugInner;
    public readonly int DeskTop;
    public readonly int DeskFront;
    public readonly int WindowFrame;
    public readonly int BoardBg;
    public readonly string SkylineStyle;

    public readonly int WeatherDawn;
    public readonly int WeatherDay;
    public readonly int WeatherSunset;
    public readonly int WeatherMidnightRain;
    public readonly int WeatherStormAlert;

    public ResolvedScenePalette(ThemePack theme)
    {
        Background = ColorUtil.ParseHex(theme.Palette.Background, ColorUtil.FromRgb(18, 22, 34));
        Surface = ColorUtil.ParseHex(theme.Palette.Surface, ColorUtil.FromRgb(27, 34, 52));
        Border = ColorUtil.ParseHex(theme.Palette.Border, ColorUtil.FromRgb(82, 92, 118));
        Accent = ColorUtil.ParseHex(theme.Palette.Accent, ColorUtil.FromRgb(245, 158, 11));
        Text = ColorUtil.ParseHex(theme.Palette.Text, ColorUtil.FromRgb(241, 245, 249));

        WeatherDawn = ColorUtil.ParseHex(theme.Weather.Dawn, ColorUtil.FromRgb(246, 184, 121));
        WeatherDay = ColorUtil.ParseHex(theme.Weather.Day, ColorUtil.FromRgb(90, 169, 230));
        WeatherSunset = ColorUtil.ParseHex(theme.Weather.Sunset, ColorUtil.FromRgb(234, 107, 59));
        WeatherMidnightRain = ColorUtil.ParseHex(theme.Weather.MidnightRain, ColorUtil.FromRgb(26, 36, 59));
        WeatherStormAlert = ColorUtil.ParseHex(theme.Weather.StormAlert, ColorUtil.FromRgb(127, 29, 29));

        SceneColors? s = theme.Scene;
        WallTop = s?.WallTop != null ? ColorUtil.ParseHex(s.WallTop, Background) : ColorUtil.Lighten(Background, 0.06f);
        WallBottom = s?.WallBottom != null ? ColorUtil.ParseHex(s.WallBottom, Background) : Background;
        FloorBase = s?.FloorBase != null ? ColorUtil.ParseHex(s.FloorBase, Surface) : ColorUtil.Darken(Surface, 0.2f);
        FloorLine = s?.FloorLine != null ? ColorUtil.ParseHex(s.FloorLine, Border) : ColorUtil.Darken(Surface, 0.42f);
        RugOuter = s?.RugOuter != null ? ColorUtil.ParseHex(s.RugOuter, Border) : ColorUtil.Darken(Accent, 0.55f);
        RugInner = s?.RugInner != null ? ColorUtil.ParseHex(s.RugInner, Surface) : ColorUtil.Darken(Accent, 0.35f);
        DeskTop = s?.DeskTop != null ? ColorUtil.ParseHex(s.DeskTop, Border) : ColorUtil.Lighten(Surface, 0.18f);
        DeskFront = s?.DeskFront != null ? ColorUtil.ParseHex(s.DeskFront, Surface) : Surface;
        WindowFrame = s?.WindowFrame != null ? ColorUtil.ParseHex(s.WindowFrame, Border) : Border;
        BoardBg = s?.BoardBg != null ? ColorUtil.ParseHex(s.BoardBg, Surface) : ColorUtil.Darken(Surface, 0.12f);

        string style = (s?.SkylineStyle ?? InferSkylineStyle(theme.Id)).Trim().ToLowerInvariant();
        SkylineStyle = style is ("konoha" or "cyber" or "lofi") ? style : "konoha";
    }

    private static string InferSkylineStyle(string themeId)
    {
        if (themeId.Contains("cyber", StringComparison.OrdinalIgnoreCase)) return "cyber";
        if (themeId.Contains("lofi", StringComparison.OrdinalIgnoreCase) || themeId.Contains("cozy", StringComparison.OrdinalIgnoreCase)) return "lofi";
        return "konoha";
    }
}
