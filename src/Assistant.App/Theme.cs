using System.Windows;
using System.Windows.Media;
using Assistant.Core.Storage;

namespace Assistant.App;

/// <summary>
/// Puts the look into the app's resources (Theme.xaml holds the styles): light or dark, with the character's Normal
/// color as the accent, so the chat bubble, menus, Settings and the setup wizard all match the character.
/// Every window refers to these by DynamicResource, so a change in Settings shows everywhere at once.
/// </summary>
public static class Theme
{
    /// <summary>Onest, bundled with the app (with Cyrillic); Segoe UI fills in anything it lacks, such as √ and π.</summary>
    public static readonly FontFamily UiFont = new(new Uri("pack://application:,,,/"), "./Fonts/#Onest, Segoe UI");

    public static void Apply(ResourceDictionary resources, Appearance look)
    {
        bool dark = look.BubbleTheme == "dark";
        Color paper = Hex(dark ? "#1B2029" : "#FFFFFF");
        Color ink = Hex(dark ? "#E9EDF4" : "#1C2330");
        Color accent = look.MoodColors.TryGetValue("Idle", out string? idle) && Appearance.IsColor(idle) ? Hex(idle) : Hex("#4A90E2");

        resources["UiFont"] = UiFont;
        Set(resources, "Paper", paper);
        Set(resources, "Ink", ink);
        Set(resources, "Muted", Hex(dark ? "#9AA3B2" : "#5F6878"));
        Set(resources, "Line", Hex(dark ? "#2E3542" : "#E4E8EF"));
        Set(resources, "Field", Hex(dark ? "#232935" : "#F5F7FA"));
        Set(resources, "Hover", Hex(dark ? "#2A3140" : "#EEF2F7"));
        Set(resources, "Accent", accent);
        Set(resources, "AccentTint", Mix(accent, paper, dark ? 0.24 : 0.14));
        // Accent-colored text must stay readable on the paper: darker in light mode, lighter in dark mode.
        Set(resources, "AccentInk", dark ? Mix(accent, Colors.White, 0.6) : Mix(accent, ink, 0.7));
        Set(resources, "OnAccent", Contrast(Colors.White, accent) >= 3 ? Colors.White : Hex("#1C2330"));
        Set(resources, "Warn", Hex(dark ? "#E8A84A" : "#C77A12"));
        Set(resources, "WarnTint", Hex(dark ? "#3A2E17" : "#FFF4E0"));
        Set(resources, "Bad", Hex(dark ? "#F07C72" : "#C8382E"));
        Set(resources, "Good", Hex(dark ? "#5CC98A" : "#2E8B57"));
    }

    static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>share of a, the rest b.</summary>
    static Color Mix(Color a, Color b, double share) => Color.FromRgb(
        (byte)Math.Round(a.R * share + b.R * (1 - share)),
        (byte)Math.Round(a.G * share + b.G * (1 - share)),
        (byte)Math.Round(a.B * share + b.B * (1 - share)));

    static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static double Luminance(Color c)
    {
        static double Channel(byte value)
        {
            double v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
