using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Core.Diagnostics;
using Assistant.Core.Storage;

namespace Assistant.Core.Characters;

/// <summary>What one mood slot of a custom character holds.</summary>
public sealed record SlotContent(SlotKind Kind, IReadOnlyList<string> Files)
{
    public static readonly SlotContent Empty = new(SlotKind.Empty, []);
}

/// <summary>Empty; one animated GIF; or PNG files, played in name order (one PNG is a still picture).</summary>
public enum SlotKind { Empty, Gif, Frames }

/// <summary>
/// Custom characters in Documents\Assistant\characters: one folder per character, with one folder per mood
/// ("Idle", "Thinking", ...) holding an animated GIF or numbered PNG frames. Only Idle is needed; a mood without
/// files shows the Idle animation with the mood's label and a glow in the mood's color.
/// Plain folders, so files can be dropped in by hand as well as through Settings.
/// </summary>
public sealed partial class CharacterPacks(string folder)
{
    public const int MaxFrames = 240;
    public const long MaxFileBytes = 30 * 1024 * 1024;
    public const int DefaultFramesPerSecond = 12;
    const string PackFile = "character.json";

    /// <summary>The mood slots, in the order Settings shows them. Folder names match the mood names.</summary>
    public static IReadOnlyList<string> Slots { get; } = Appearance.DefaultColors.Keys.ToList();

    public string Folder { get; } = folder;

    public string PackFolder(string name) => Path.Combine(Folder, name);

    public string SlotFolder(string name, string slot) => Path.Combine(Folder, name, slot);

    /// <summary>A folder name: letters, digits, spaces, dashes and underscores, up to 40 characters.</summary>
    public static bool IsValidName(string name) => name.Length is > 0 and <= 40 && NamePattern().IsMatch(name) && name.Trim() == name;

    /// <summary>The custom characters, by folder name.</summary>
    public IReadOnlyList<string> Names()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.GetDirectories(Folder)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(IsValidName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool Exists(string name) => IsValidName(name) && Directory.Exists(PackFolder(name));

    /// <summary>
    /// Makes a new character with an empty folder for every mood and a how-to, and returns its folder name
    /// ("My character", or "My character 2" when that one exists).
    /// </summary>
    public string Create(string wanted)
    {
        string baseName = Regex.Replace(wanted.Trim(), @"[^\p{L}\p{N} _-]", "").Trim();
        if (baseName.Length == 0) baseName = "My character";
        if (baseName.Length > 36) baseName = baseName[..36].Trim();

        string name = baseName;
        for (int number = 2; Directory.Exists(PackFolder(name)); number++) name = $"{baseName} {number}";

        foreach (string slot in Slots) Directory.CreateDirectory(SlotFolder(name, slot));
        File.WriteAllText(Path.Combine(PackFolder(name), PackFile),
            JsonSerializer.Serialize(new { framesPerSecond = DefaultFramesPerSecond }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(PackFolder(name), "README.txt"), HowTo);
        return name;
    }

    /// <summary>How fast PNG frames play, from character.json (GIFs keep their own timing).</summary>
    public int FramesPerSecond(string name)
    {
        try
        {
            string file = Path.Combine(PackFolder(name), PackFile);
            if (!File.Exists(file)) return DefaultFramesPerSecond;
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(file));
            return json.RootElement.TryGetProperty("framesPerSecond", out JsonElement fps) && fps.TryGetInt32(out int value)
                ? Math.Clamp(value, 1, 60)
                : DefaultFramesPerSecond;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return DefaultFramesPerSecond;
        }
    }

    /// <summary>What a slot holds now. A GIF wins over PNGs if both were dropped in.</summary>
    public SlotContent Slot(string name, string slot)
    {
        string path = SlotFolder(name, slot);
        if (!Directory.Exists(path)) return SlotContent.Empty;
        try
        {
            string? gif = Directory.GetFiles(path, "*.gif").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (gif is not null) return new SlotContent(SlotKind.Gif, [gif]);

            var pngs = Directory.GetFiles(path, "*.png").OrderBy(NaturalKey, StringComparer.OrdinalIgnoreCase).Take(MaxFrames).ToList();
            return pngs.Count > 0 ? new SlotContent(SlotKind.Frames, pngs) : SlotContent.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Could not read a character folder", ex);
            return SlotContent.Empty;
        }
    }

    /// <summary>
    /// Changes when files in the slot change, so the character can tell its cached animation is out of date
    /// (also after files are dropped in by hand).
    /// </summary>
    public string Stamp(string name, string slot)
    {
        SlotContent content = Slot(name, slot);
        return string.Join("|", content.Files.Select(file => $"{file}:{File.GetLastWriteTimeUtc(file).Ticks}"));
    }

    /// <summary>
    /// Checks files the user picked for a slot: one GIF, or one or more PNGs. Returns a problem to show, or null.
    /// </summary>
    public static string? Check(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return "No file was chosen.";
        var kinds = files.Select(file => Path.GetExtension(file).ToLowerInvariant()).Distinct().ToList();
        if (kinds.Any(kind => kind is not (".gif" or ".png"))) return "Only .gif and .png files can be used.";
        if (kinds.Count > 1) return "Choose either one GIF, or PNG frames, not both.";
        if (kinds[0] == ".gif" && files.Count > 1) return "Choose one GIF per mood (or several PNG frames).";
        if (files.Count > MaxFrames) return $"That's {files.Count} frames; up to {MaxFrames} fit in one mood.";
        if (files.FirstOrDefault(file => new FileInfo(file).Length > MaxFileBytes) is { } big)
            return $"{Path.GetFileName(big)} is over {MaxFileBytes / 1024 / 1024} MB.";
        return null;
    }

    /// <summary>
    /// Copies the picked files into the slot, after the old ones were removed (by the caller, to the Recycle Bin).
    /// PNGs are renamed 001.png, 002.png... in name order, so they play in the order the user expects.
    /// </summary>
    public void Fill(string name, string slot, IReadOnlyList<string> files)
    {
        string target = SlotFolder(name, slot);
        Directory.CreateDirectory(target);
        if (files.Count == 1 && Path.GetExtension(files[0]).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(files[0], Path.Combine(target, "animation.gif"), overwrite: true);
            return;
        }

        int number = 1;
        foreach (string file in files.OrderBy(file => NaturalKey(Path.GetFileName(file)), StringComparer.OrdinalIgnoreCase))
            File.Copy(file, Path.Combine(target, $"{number++:000}.png"), overwrite: true);
    }

    /// <summary>The animation files in a slot (what Clear removes). Other files the user put there are left alone.</summary>
    public IReadOnlyList<string> SlotFiles(string name, string slot)
    {
        string path = SlotFolder(name, slot);
        if (!Directory.Exists(path)) return [];
        return Directory.GetFiles(path, "*.gif").Concat(Directory.GetFiles(path, "*.png")).ToList();
    }

    // "frame2.png" sorts before "frame10.png": every number is padded to 10 digits for sorting.
    static string NaturalKey(string path) =>
        Digits().Replace(Path.GetFileName(path), match => match.Value.PadLeft(10, '0'));

    [GeneratedRegex(@"^[\p{L}\p{N} _-]+$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    const string HowTo = """
        Your own character
        ==================

        Every folder here is one mood. Put ONE of these in a folder:
          - an animated GIF, or
          - PNG frames, numbered in the order they play (001.png, 002.png, ...). One PNG is a still picture.

        Transparent backgrounds look best: the character floats over your desktop.
        Square-ish pictures around 256 x 256 pixels are ideal; bigger ones are shrunk.

        Only Idle is needed. A mood without files shows the Idle animation, with the mood's label
        ("thinking...") and a glow in the mood's color (Settings > Character > Colors).

        The moods:
          Idle        normal, nothing going on (needed)
          Thinking    working on an answer
          FoundIt     found files
          Confused    found nothing
          Happy       something went well
          Tired       the cloud chat is unavailable
          Sleepy      no use for 10 minutes
          Review      the weekly review is waiting
          HandRaised  "!" - it thinks you might be stuck
          Listening   push-to-talk is held
          Speaking    reading a reply aloud
          TimeUp      a timer you set is due

        PNG frames play at the speed in character.json ("framesPerSecond", 1 to 60). GIFs keep their own speed.
        You can also fill the moods in Settings > Character, which copies the files here for you.
        Changes show the next time the character changes mood.
        """;
}
