using System.Windows;
using System.Windows.Media;

namespace HoshinoEditor.Services;

public static class ThemeService
{
    private sealed record Palette(Color Accent, Color AccentBright, Color AccentStrong, Color Background, Color Raised, Color Chrome, Color Text, Color TextDim, Color TextFaint);

    private static readonly Dictionary<string, Palette> Palettes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hoshino"] = Make("#A855F7", "#C084FC", "#9333EA", "#0A0A0F", "#0D0D14", "#12121B", "#F5F4FB", "#9B9AAC", "#6C6B7E"),
        ["Midnight"] = Make("#3B82F6", "#60A5FA", "#2563EB", "#070B14", "#0B1220", "#101827", "#F3F7FF", "#98A6BA", "#68778E"),
        ["Sakura"] = Make("#EC4899", "#F472B6", "#DB2777", "#10080F", "#180D16", "#21111D", "#FFF4FA", "#B89AAA", "#846778"),
        ["Aurora"] = Make("#14B8A6", "#2DD4BF", "#0D9488", "#06100F", "#091816", "#0D211E", "#EEFFFC", "#91B8B2", "#62847F"),
        ["Ember"] = Make("#F97316", "#FB923C", "#EA580C", "#110B07", "#1A100A", "#24160D", "#FFF8F2", "#B5A092", "#806D61")
    };

    public static IReadOnlyList<string> ThemeNames { get; } = ["Hoshino", "Midnight", "Sakura", "Aurora", "Ember", "Custom"];

    public static void ApplyCurrent()
    {
        var settings = SettingsService.Current;
        var palette = settings.Theme.Equals("Custom", StringComparison.OrdinalIgnoreCase)
            ? Make(settings.CustomAccent, Lighten(Parse(settings.CustomAccent), .18).ToString(), Darken(Parse(settings.CustomAccent), .15).ToString(), "#0A0A0F", "#0D0D14", "#12121B", "#F5F4FB", "#9B9AAC", "#6C6B7E")
            : Palettes.GetValueOrDefault(settings.Theme, Palettes["Hoshino"]);

        SetBrush("Bg", palette.Background); SetBrush("BgRaised", palette.Raised); SetBrush("BgChrome", palette.Chrome);
        SetBrush("Text", palette.Text); SetBrush("TextDim", palette.TextDim); SetBrush("TextFaint", palette.TextFaint);
        SetBrush("Accent", palette.Accent); SetBrush("AccentBright", palette.AccentBright); SetBrush("AccentStrong", palette.AccentStrong);
        SetBrush("AccentWash", Color.FromArgb(0x1F, palette.Accent.R, palette.Accent.G, palette.Accent.B));
        SetBrush("AccentWash2", Color.FromArgb(0x33, palette.Accent.R, palette.Accent.G, palette.Accent.B));
        SetBrush("AccentLine", Color.FromArgb(0x73, palette.Accent.R, palette.Accent.G, palette.Accent.B));
        SetGradient("AccentGradient", palette.AccentBright, palette.AccentStrong);
        SetGradient("AppBackground", palette.Background, Darken(palette.Accent, .78));
    }

    private static Palette Make(string accent, string bright, string strong, string bg, string raised, string chrome, string text, string dim, string faint) =>
        new(Parse(accent), Parse(bright), Parse(strong), Parse(bg), Parse(raised), Parse(chrome), Parse(text), Parse(dim), Parse(faint));

    private static Color Parse(string value)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch { return (Color)ColorConverter.ConvertFromString("#A855F7"); }
    }

    private static Color Lighten(Color color, double amount) => Blend(color, Colors.White, amount);
    private static Color Darken(Color color, double amount) => Blend(color, Colors.Black, amount);
    private static Color Blend(Color a, Color b, double amount) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * amount), (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount));

    private static void SetBrush(string key, Color color)
    {
        Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private static void SetGradient(string key, Color first, Color second)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(first, 0));
        brush.GradientStops.Add(new GradientStop(Blend(first, second, .55), .55));
        brush.GradientStops.Add(new GradientStop(second, 1));
        Application.Current.Resources[key] = brush;
    }
}
