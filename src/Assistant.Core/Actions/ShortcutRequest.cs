using System.Text.RegularExpressions;

namespace Assistant.Core.Actions;

/// <summary>What a chat message asks of the shortcuts: add one, remove one, or list them.</summary>
public abstract record ShortcutRequest
{
    public sealed record Add(string Keys, string Command) : ShortcutRequest;
    public sealed record Remove(string Keys) : ShortcutRequest;
    public sealed record List : ShortcutRequest;
}

/// <summary>
/// The app's side of shortcuts: saves them to config.json and registers the keys with Windows. Each method returns the
/// reply to show ("Done: Alt+S now runs “open spotify”." or why it couldn't).
/// </summary>
public interface IShortcutBook
{
    string Add(string keys, string command);
    string Remove(string keys);
    string List();
}

/// <summary>
/// "make Alt+S open Spotify", "set Ctrl+Alt+P to pause", "add a shortcut Alt+T for set a timer for 25 minutes",
/// "remove the Alt+S shortcut", "my shortcuts"; Bulgarian too ("направи Alt+S да отваря Spotify"). Answered by code.
/// </summary>
public static partial class ShortcutRequests
{
    // Bulgarian "да отваря" (it opens) → "отвори" (open), the form the actions understand.
    static readonly Dictionary<string, string> Imperative = new()
    {
        ["отваря"] = "отвори", ["пуска"] = "пусни", ["спира"] = "спри", ["показва"] = "покажи", ["намалява"] = "намали",
        ["увеличава"] = "увеличи", ["заключва"] = "заключи", ["прави"] = "направи", ["търси"] = "потърси", ["изпразва"] = "изпразни",
        ["слага"] = "сложи", ["включва"] = "включи", ["изключва"] = "изключи",
    };

    public static ShortcutRequest? FromMessage(string message)
    {
        string text = message.Trim().TrimEnd('.', '!', '?');
        if (text.Length is 0 or > 200) return null;

        if (ListShortcuts().IsMatch(text)) return new ShortcutRequest.List();
        if (RemoveShortcut().Match(text) is { Success: true } remove && HotkeyText.Normalize(remove.Groups["keys"].Value) is { } removeKeys)
            return new ShortcutRequest.Remove(removeKeys);
        if (AddShortcut().Match(text) is { Success: true } add && HotkeyText.Normalize(add.Groups["keys"].Value) is { } keys)
        {
            string command = add.Groups["command"].Value.Trim().Trim('"', '“', '”', '\'');
            string[] words = command.Split(' ', 2);
            if (Imperative.TryGetValue(words[0].ToLowerInvariant(), out string? verb))
                command = verb + (words.Length > 1 ? " " + words[1] : "");
            return command.Length == 0 ? null : new ShortcutRequest.Add(keys, command);
        }
        return null;
    }

    const string Keys = @"(?<keys>(ctrl|control|alt|shift|win|windows)(\s*\+\s*[\w]+)+)";

    [GeneratedRegex(@"^(please\s|моля\s)?(make|set|bind|map|assign|add\s(a\s)?(shortcut|hotkey)(\sfor)?|create\s(a\s)?(shortcut|hotkey)(\sfor)?|направи|задай|сложи|добави(\s(пряк\sпът|клавишна\sкомбинация|бърза\sклавишна\sкомбинация))?)\s*:?\s+" + Keys + @"\s+(to\s+|for\s+|that\s+|run\s+|runs\s+|да\s+|за\s+)?(?<command>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AddShortcut();

    [GeneratedRegex(@"^(please\s|моля\s)?(remove|delete|clear|forget|премахни|изтрий|махни)\s+(the\s+)?(shortcut\s+|hotkey\s+|прекия\sпът\s+|комбинацията\s+)?" + Keys + @"(\s+(shortcut|hotkey))?$", RegexOptions.IgnoreCase)]
    private static partial Regex RemoveShortcut();

    [GeneratedRegex(@"^(what\sare\s|show\s|list\s)?(my\s|the\s)?(shortcuts|hotkeys|keyboard\sshortcuts)$|^(какви\sса\s|покажи\s)?(моите\s)?(преки\sпътища|клавишни\sкомбинации)$", RegexOptions.IgnoreCase)]
    private static partial Regex ListShortcuts();
}

/// <summary>Key combinations as text: "ctrl + alt+p" → "Ctrl+Alt+P". Needs Ctrl, Alt or Win, so typing can't set one off.</summary>
public static class HotkeyText
{
    static readonly string[] Order = ["Ctrl", "Alt", "Shift", "Win"];

    /// <summary>The tidy form, or null if it isn't a usable combination (no Ctrl/Alt/Win, no key, or two keys).</summary>
    public static string? Normalize(string keys)
    {
        var modifiers = new HashSet<string>();
        string? key = null;
        foreach (string raw in keys.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.ToLowerInvariant();
            switch (part)
            {
                case "ctrl" or "control": modifiers.Add("Ctrl"); break;
                case "alt": modifiers.Add("Alt"); break;
                case "shift": modifiers.Add("Shift"); break;
                case "win" or "windows": modifiers.Add("Win"); break;
                default:
                    if (key is not null) return null; // two keys
                    key = part.Length == 1 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..];
                    break;
            }
        }
        if (key is null || !(modifiers.Contains("Ctrl") || modifiers.Contains("Alt") || modifiers.Contains("Win"))) return null;
        return string.Join("+", Order.Where(modifiers.Contains).Append(key));
    }

    /// <summary>Same combination, however it was written.</summary>
    public static bool Same(string a, string b) =>
        string.Equals(Normalize(a) ?? a, Normalize(b) ?? b, StringComparison.OrdinalIgnoreCase);
}
