using System.IO;
using System.Text.Json;
using System.Windows;

namespace Assistant.App;

/// <summary>Where the character sits on screen, kept in placement.json in the data folder.</summary>
public sealed class CharacterPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }

    /// <summary>The saved position, or null when there is none or it is off every screen (a monitor was unplugged).</summary>
    public static Point? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            CharacterPlacement? saved = JsonSerializer.Deserialize<CharacterPlacement>(File.ReadAllText(path));
            if (saved is not null && IsOnScreen(saved.Left, saved.Top)) return new Point(saved.Left, saved.Top);
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        return null;
    }

    public static void Save(string path, double left, double top)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new CharacterPlacement { Left = left, Top = top }));
        }
        catch (IOException)
        {
            // Losing the position is harmless: the character starts in the corner next time.
        }
    }

    static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50
        && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
        && top >= SystemParameters.VirtualScreenTop - 50
        && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;
}
