namespace Assistant.Core.Activity;

/// <summary>Readable names for common .exe files ("Code.exe" is "Visual Studio Code"), and which ones are browsers.</summary>
public static class AppNames
{
    static readonly Dictionary<string, string> Friendly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Code.exe"] = "Visual Studio Code",
        ["devenv.exe"] = "Visual Studio",
        ["rider64.exe"] = "Rider",
        ["Unity.exe"] = "Unity",
        ["chrome.exe"] = "Chrome",
        ["msedge.exe"] = "Edge",
        ["firefox.exe"] = "Firefox",
        ["opera.exe"] = "Opera",
        ["brave.exe"] = "Brave",
        ["vivaldi.exe"] = "Vivaldi",
        ["WINWORD.EXE"] = "Word",
        ["EXCEL.EXE"] = "Excel",
        ["POWERPNT.EXE"] = "PowerPoint",
        ["Discord.exe"] = "Discord",
        ["Spotify.exe"] = "Spotify",
        ["steam.exe"] = "Steam",
        ["blender.exe"] = "Blender",
        ["Photoshop.exe"] = "Photoshop",
        ["notepad.exe"] = "Notepad",
        ["WindowsTerminal.exe"] = "Windows Terminal",
    };

    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe",
    };

    public static string Name(string exe) =>
        Friendly.TryGetValue(exe, out string? name) ? name
        : exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;

    public static bool IsBrowser(string exe) => Browsers.Contains(exe);
}
