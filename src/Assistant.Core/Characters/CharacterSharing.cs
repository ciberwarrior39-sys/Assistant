using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Core.Characters;

/// <summary>What a character file holds, shown before importing it. Idle is its first picture, for a preview.</summary>
public sealed record SharedCharacter(string Name, CharacterInfo Info, int MoodsFilled, byte[]? Idle, int Skipped);

/// <summary>
/// One-file characters: a .assistant-character file is a zip of a character's folder (character.json, README.txt,
/// and a folder per mood with a GIF or PNG frames).
/// A file from someone else is untrusted, so importing only ever takes pictures and the small text files,
/// into the new character's own folder, within size limits. Nothing in it is run.
/// </summary>
public static partial class CharacterSharing
{
    public const string Extension = ".assistant-character";
    public const long MaxTotalBytes = 200L * 1024 * 1024;
    const long MaxTextBytes = 64 * 1024;
    const int MaxEntries = 4000;

    /// <summary>Writes the character to one file. Colors are the sender's glow colors, offered to the receiver.</summary>
    public static void Export(CharacterPacks packs, string name, IReadOnlyDictionary<string, string>? colors, string target)
    {
        string partial = target + ".partial";
        using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
        {
            WriteText(zip, CharacterPacks.InfoFile, packs.ReadInfo(name).ToJson(colors));
            WriteText(zip, "README.txt", CharacterPacks.ReadMe);
            foreach (string slot in CharacterPacks.Slots)
            {
                SlotContent content = packs.Slot(name, slot);
                foreach (string file in content.Files)
                    zip.CreateEntryFromFile(file, $"{slot}/{Path.GetFileName(file)}", CompressionLevel.Optimal);
                if (content.Kind == SlotKind.Empty) zip.CreateEntry(slot + "/"); // keeps the empty mood folder
            }
        }
        File.Move(partial, target, overwrite: true);
    }

    /// <summary>Reads a character file without unpacking it: name, author, how many moods, the Idle picture.</summary>
    public static SharedCharacter Inspect(string file)
    {
        using ZipArchive zip = ZipFile.OpenRead(file);
        List<Accepted> accepted = Accept(zip, out int skipped);

        CharacterInfo info = accepted.FirstOrDefault(entry => entry.Slot is null && entry.Name == CharacterPacks.InfoFile) is { } json
            ? CharacterInfo.Parse(ReadText(json.Entry))
            : CharacterInfo.Default;
        int moods = accepted.Where(entry => entry.Slot is not null).Select(entry => entry.Slot).Distinct().Count();

        byte[]? idle = null;
        Accepted? first = accepted.Where(entry => entry.Slot == "Idle")
            .OrderBy(entry => Path.GetExtension(entry.Name) == ".gif" ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (first is not null) idle = ReadBytes(first.Entry, CharacterPacks.MaxFileBytes);

        string name = Path.GetFileNameWithoutExtension(file);
        return new SharedCharacter(name, info, moods, idle, skipped);
    }

    /// <summary>Unpacks a character file into a new character folder and returns its name (never overwrites one).</summary>
    public static string Import(CharacterPacks packs, string file)
    {
        using ZipArchive zip = ZipFile.OpenRead(file);
        List<Accepted> accepted = Accept(zip, out _);
        if (!accepted.Any(entry => entry.Slot is not null))
            throw new InvalidDataException("There are no animations in this file.");

        string name = packs.FreeName(Path.GetFileNameWithoutExtension(file));
        string folder = packs.PackFolder(name);
        try
        {
            foreach (string slot in CharacterPacks.Slots) Directory.CreateDirectory(packs.SlotFolder(name, slot));
            long total = 0;
            foreach (Accepted entry in accepted)
            {
                string target = entry.Slot is null ? Path.Combine(folder, entry.Name) : Path.Combine(packs.SlotFolder(name, entry.Slot), entry.Name);
                long limit = entry.Slot is null ? MaxTextBytes : CharacterPacks.MaxFileBytes;
                total += CopyLimited(entry.Entry, target, Math.Min(limit, MaxTotalBytes - total));
            }
            // A file without its own character.json or README still gets them.
            if (!File.Exists(Path.Combine(folder, CharacterPacks.InfoFile))) packs.WriteInfo(name, CharacterInfo.Default);
            if (!File.Exists(Path.Combine(folder, "README.txt"))) File.WriteAllText(Path.Combine(folder, "README.txt"), CharacterPacks.ReadMe);
            return name;
        }
        catch
        {
            // A half-imported character would only confuse: remove it and report the problem.
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
            throw;
        }
    }

    /// <summary>One zip entry that may be unpacked, and where: a mood folder, or the character's own folder (Slot null).</summary>
    sealed record Accepted(ZipArchiveEntry Entry, string? Slot, string Name);

    // Only "character.json", "README.txt" and "<Mood>/<name>.gif|png" are taken, with safe names,
    // up to the frame limit per mood. A zip made of the folder itself ("Wizard Cat/Idle/...") works too.
    static List<Accepted> Accept(ZipArchive zip, out int skipped)
    {
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("This file has too many entries to be a character.");

        string prefix = CommonFolder(zip);
        var accepted = new List<Accepted>();
        var perSlot = new Dictionary<string, int>();
        skipped = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue; // a folder
            string path = entry.FullName.Replace('\\', '/');
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) { skipped++; continue; }
            string[] parts = path[prefix.Length..].Split('/');

            if (parts.Length == 1 && (parts[0].Equals(CharacterPacks.InfoFile, StringComparison.OrdinalIgnoreCase)
                                      || parts[0].Equals("README.txt", StringComparison.OrdinalIgnoreCase)))
            {
                accepted.Add(new Accepted(entry, null, parts[0].Equals("README.txt", StringComparison.OrdinalIgnoreCase) ? "README.txt" : CharacterPacks.InfoFile));
                continue;
            }

            string? slot = parts.Length == 2 ? CharacterPacks.Slots.FirstOrDefault(s => s.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) : null;
            string extension = Path.GetExtension(parts[^1]).ToLowerInvariant();
            if (slot is null || extension is not (".gif" or ".png") || !SafeFileName().IsMatch(parts[1])
                || ReservedName().IsMatch(Path.GetFileNameWithoutExtension(parts[1])) || entry.Length > CharacterPacks.MaxFileBytes)
            {
                skipped++;
                continue;
            }
            int count = perSlot.GetValueOrDefault(slot);
            if (count >= CharacterPacks.MaxFrames) { skipped++; continue; }
            perSlot[slot] = count + 1;
            accepted.Add(new Accepted(entry, slot, parts[1]));
        }
        return accepted;
    }

    // "Wizard Cat/" when every entry sits in one folder that isn't a mood (someone zipped the folder itself).
    static string CommonFolder(ZipArchive zip)
    {
        var firsts = zip.Entries.Select(entry => entry.FullName.Replace('\\', '/'))
            .Where(path => path.Contains('/'))
            .Select(path => path[..path.IndexOf('/')])
            .Distinct()
            .ToList();
        bool allInside = zip.Entries.All(entry => entry.FullName.Replace('\\', '/').Contains('/'));
        return allInside && firsts.Count == 1 && !CharacterPacks.Slots.Contains(firsts[0], StringComparer.OrdinalIgnoreCase)
            ? firsts[0] + "/"
            : "";
    }

    // Copies at most limit bytes, whatever size the zip claims (a lying entry can't fill the disk).
    static long CopyLimited(ZipArchiveEntry entry, string target, long limit)
    {
        using Stream input = entry.Open();
        using FileStream output = File.Create(target);
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += read;
            if (copied > limit) throw new InvalidDataException($"{entry.Name} is bigger than allowed.");
            output.Write(buffer, 0, read);
        }
        return copied;
    }

    static byte[]? ReadBytes(ZipArchiveEntry entry, long limit)
    {
        using var memory = new MemoryStream();
        using Stream input = entry.Open();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + read > limit) return null;
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    static string ReadText(ZipArchiveEntry entry) =>
        ReadBytes(entry, MaxTextBytes) is { } bytes ? Encoding.UTF8.GetString(bytes) : "";

    static void WriteText(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    // Letters, digits, spaces, dots, dashes, underscores: no folders, no ".." tricks, no device names.
    [GeneratedRegex(@"^(?!\.)[\p{L}\p{N} ._-]{1,80}$")]
    private static partial Regex SafeFileName();

    // Names Windows keeps for devices, even with an extension ("CON.png").
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM\d|LPT\d)(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();
}
