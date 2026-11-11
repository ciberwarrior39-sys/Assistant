using System.Text.Json;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Backup;

/// <summary>What one backup did, for the chat, Settings and the log (never file names in the log).</summary>
public sealed record BackupOutcome
{
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Removed { get; init; }           // deleted on the PC: the backup's copy moved to Older versions
    public int Skipped { get; init; }           // in use or not readable: tried again next time
    public int CloudOnly { get; init; }         // OneDrive files not on this PC: not downloaded just to back them up
    public long Bytes { get; init; }            // copied this time
    public IReadOnlyList<string> MissingFolders { get; init; } = [];
    public string? Problem { get; init; }       // stopped before finishing (no room, destination gone…)
    public bool Cancelled { get; init; }
    public TimeSpan Took { get; init; }

    public bool Finished => Problem is null && !Cancelled;

    /// <summary>"12 new or changed files (340 MB), 2 removed" in plain words.</summary>
    public string Summary()
    {
        if (Problem is not null) return Problem;
        var parts = new List<string>();
        int changed = Added + Updated;
        parts.Add(changed == 0 ? "nothing new or changed" : $"{changed:N0} new or changed file{(changed == 1 ? "" : "s")} ({BackupEngine.Size(Bytes)})");
        if (Removed > 0) parts.Add($"{Removed:N0} deleted on the PC (kept 30 days in Older versions)");
        if (Skipped > 0) parts.Add($"{Skipped:N0} in use, tried again next time");
        if (CloudOnly > 0) parts.Add($"{CloudOnly:N0} only in OneDrive's cloud, left there");
        if (MissingFolders.Count > 0) parts.Add($"couldn't find {string.Join(", ", MissingFolders)}");
        string text = string.Join("; ", parts);
        return (Cancelled ? "Stopped part-way: " : "") + char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }
}

/// <summary>A drive a backup can go to, for setup: "E:\", "SanDisk", free bytes, whether it's a USB / removable one.</summary>
public sealed record BackupDrive(string Root, string Name, long FreeBytes, bool Removable);

/// <summary>
/// Backups as plain files anyone can open, no app needed to get them back:
/// <c>&lt;destination&gt;\Assistant Backup\&lt;PC name&gt;\Current\&lt;folder&gt;\…</c> is the latest copy, and when a file changes or is
/// deleted on the PC, the backup's previous copy moves to <c>Older versions\&lt;date and time&gt;\&lt;folder&gt;\…</c>, kept 30 days,
/// so a mistake (or ransomware scrambling files) can be undone. Only new and changed files are copied (by size and time).
/// Files in use are skipped and tried next time; OneDrive files that are only in the cloud are left there (backing them
/// up would download them all). Copies are written next to the target first and renamed, so a pulled-out USB drive
/// never leaves a half-written file in Current.
/// </summary>
public static class BackupEngine
{
    public const string FolderName = "Assistant Backup";
    public const string CurrentFolder = "Current";
    public const string OlderFolder = "Older versions";
    public static readonly TimeSpan KeepOlder = TimeSpan.FromDays(30);
    const string MarkerFile = "backup-id.txt";
    const string StampFormat = "yyyy-MM-dd HH-mm-ss";
    const long SpareBytes = 200L << 20; // left free on the destination
    static readonly TimeSpan SameTime = TimeSpan.FromSeconds(2); // FAT and exFAT keep times to 2 seconds

    // Rebuilt by the tools that made them, and often huge: not worth backing up.
    static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".vs", "__pycache__", ".gradle", ".cache", "$RECYCLE.BIN", "System Volume Information",
    };

    // Windows' "this file is only in the cloud" marks (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS, …_RECALL_ON_OPEN, OFFLINE).
    const FileAttributes CloudOnlyMarks = (FileAttributes)0x400000 | (FileAttributes)0x40000 | FileAttributes.Offline;

    /// <summary>Documents, Desktop and Pictures: what's backed up until the user changes it.</summary>
    public static IReadOnlyList<string> DefaultFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
    ];

    /// <summary>This PC's part of a destination (several PCs can share one drive).</summary>
    public static string PcFolder(string destination) => Path.Combine(destination, FolderName, Environment.MachineName);

    /// <summary>
    /// Where the backup goes right now: the destination if it's there, or the same folder on another drive letter
    /// (USB drives get a different letter now and then), recognised by this backup's id. Null when it isn't plugged in.
    /// </summary>
    public static string? Resolve(string destination, string id)
    {
        if (destination.Length == 0) return null;
        try
        {
            if (Directory.Exists(destination)) return destination;
            string? root = Path.GetPathRoot(destination);
            if (string.IsNullOrEmpty(root) || id.Length == 0) return null;
            string inside = destination[root.Length..];
            foreach (DriveInfo drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
            {
                string candidate = Path.Combine(drive.RootDirectory.FullName, inside);
                if (IsMarked(candidate, id)) return candidate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A drive that went away while looking.
        }
        return null;
    }

    static bool IsMarked(string destination, string id)
    {
        try
        {
            string marker = Path.Combine(PcFolder(destination), MarkerFile);
            return File.Exists(marker) && File.ReadAllText(marker).Trim() == id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Drives a backup could go to: USB and external ones first, then other drives that aren't Windows' own.</summary>
    public static IReadOnlyList<BackupDrive> Drives()
    {
        string system = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var drives = new List<BackupDrive>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.RootDirectory.FullName.Equals(system, StringComparison.OrdinalIgnoreCase)) continue;
                if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed or DriveType.Network)) continue;
                string name = drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : drive.DriveType == DriveType.Removable ? "USB drive" : "Drive";
                drives.Add(new BackupDrive(drive.RootDirectory.FullName, name, drive.AvailableFreeSpace, drive.DriveType == DriveType.Removable));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not ready after all.
            }
        }
        return drives.OrderByDescending(drive => drive.Removable).ThenBy(drive => drive.Root).ToList();
    }

    /// <summary>
    /// A warning when the backup would sit on the same drive as files it backs up (a dead drive takes both), unless it's
    /// a folder that syncs to the cloud (OneDrive and the like). Null when it's fine.
    /// </summary>
    public static string? SameDriveWarning(IEnumerable<string> folders, string destination)
    {
        if (destination.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) || destination.Contains("Dropbox", StringComparison.OrdinalIgnoreCase)
            || destination.Contains("Google Drive", StringComparison.OrdinalIgnoreCase))
            return null;
        string? target = Path.GetPathRoot(destination);
        return folders.Any(folder => string.Equals(Path.GetPathRoot(folder), target, StringComparison.OrdinalIgnoreCase))
            ? "The backup is on the same drive as files it backs up, so if that drive fails, both are lost. A USB or external drive is safer."
            : null;
    }

    /// <summary>A short, unique name for each folder in the backup: "Documents", "Projects", "Projects (D)".</summary>
    public static IReadOnlyList<(string Folder, string Label)> Labels(IEnumerable<string> folders)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var labels = new List<(string, string)>();
        foreach (string folder in folders.Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(folder);
            if (name.Length == 0) name = "Drive " + folder.TrimEnd(':', '\\'); // a whole drive: "D:\"
            string label = name;
            if (used.Contains(label)) label = $"{name} ({(Path.GetPathRoot(folder) ?? "").TrimEnd(':', '\\')})";
            for (int n = 2; used.Contains(label); n++) label = $"{name} {n}";
            used.Add(label);
            labels.Add((folder, label));
        }
        return labels;
    }

    /// <summary>Backs the folders up to the destination. Never throws for a file or folder that can't be read.</summary>
    public static BackupOutcome Run(IReadOnlyList<string> folders, string destination, string id, DateTime now, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        string pc = PcFolder(destination);
        string current = Path.Combine(pc, CurrentFolder);
        string older = Path.Combine(pc, OlderFolder, now.ToString(StampFormat));
        try
        {
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(pc, MarkerFile), id);
            WriteReadMe(destination);
            PruneOlder(Path.Combine(pc, OlderFolder), now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BackupOutcome { Problem = $"Couldn't write to {destination}: {ex.Message}", Took = DateTime.UtcNow - started };
        }

        // First work out what to copy, so a full drive is found before anything is half done.
        var copies = new List<(FileInfo Source, string Target, bool Exists)>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // backup files whose original still exists (or can't be checked)
        var unreadable = new List<string>();                              // backup folders whose original couldn't be read
        var missing = new List<string>();
        var labels = Labels(folders);
        int cloudOnly = 0;
        foreach ((string folder, string label) in labels)
        {
            if (ct.IsCancellationRequested) return new BackupOutcome { Cancelled = true, Took = DateTime.UtcNow - started };
            string into = Path.Combine(current, label);
            if (!Directory.Exists(folder))
            {
                missing.Add(folder);
                unreadable.Add(into); // keep its copy: the folder may be on a drive that isn't plugged in
                continue;
            }
            foreach ((FileInfo file, string relative) in Files(folder, destination, unreadable, into, ref cloudOnly))
            {
                string target = Path.Combine(into, relative);
                keep.Add(target);
                if (IsCloudOnly(file)) continue;
                var existing = new FileInfo(target);
                bool exists = existing.Exists;
                if (exists && existing.Length == file.Length && (existing.LastWriteTimeUtc - file.LastWriteTimeUtc).Duration() <= SameTime) continue;
                copies.Add((file, target, exists));
            }
        }

        long needed = copies.Sum(copy => copy.Source.Length);
        long free = FreeBytes(destination);
        if (free >= 0 && needed > free - SpareBytes)
        {
            return new BackupOutcome
            {
                Problem = $"Not enough room: this backup needs {Size(needed)}, and {destination} has {Size(Math.Max(0, free))} free. " +
                          "Free some space there, or pick a bigger drive.",
                MissingFolders = missing,
                Took = DateTime.UtcNow - started,
            };
        }

        int added = 0, updated = 0, removed = 0, skipped = 0;
        long bytes = 0;
        bool cancelled = false;
        foreach ((FileInfo source, string target, bool exists) in copies)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string partial = target + ".partial";
                File.Copy(source.FullName, partial, overwrite: true);
                File.SetLastWriteTimeUtc(partial, source.LastWriteTimeUtc);
                if (exists) MoveToOlder(target, current, older);
                File.Move(partial, target, overwrite: true);
                if (exists) updated++;
                else added++;
                bytes += source.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                TryDelete(target + ".partial");
                if (!Directory.Exists(destination))
                    return Outcome(problem: "The backup drive went away part-way. The files copied so far are safe; the rest go next time.");
            }
        }

        // Deleted on the PC: the backup's copy moves to Older versions (only for folders read in full, and not when stopped).
        if (!cancelled)
        {
            foreach ((_, string label) in labels)
            {
                string into = Path.Combine(current, label);
                if (!Directory.Exists(into) || unreadable.Any(folder => into.StartsWith(folder, StringComparison.OrdinalIgnoreCase))) continue;
                foreach (string file in Directory.EnumerateFiles(into, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList())
                {
                    if (keep.Contains(file) || unreadable.Any(folder => file.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                    if (file.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(file);
                        continue;
                    }
                    try
                    {
                        MoveToOlder(file, current, older);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Stays in Current; tried again next time.
                    }
                }
                RemoveEmptyFolders(into);
            }
        }

        BackupOutcome result = Outcome(problem: null);
        try
        {
            File.WriteAllText(Path.Combine(pc, "last-backup.txt"), $"{now:yyyy-MM-dd HH:mm}: {result.Summary()}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a note for whoever opens the folder.
        }
        return result;

        BackupOutcome Outcome(string? problem) => new()
        {
            Added = added, Updated = updated, Removed = removed, Skipped = skipped, CloudOnly = cloudOnly, Bytes = bytes,
            MissingFolders = missing, Problem = problem, Cancelled = cancelled, Took = DateTime.UtcNow - started,
        };
    }

    // Every file under a folder, with its path inside it. Junctions (old "My Music" links and the like) aren't followed,
    // tool folders and Office's lock files are skipped, and so is the backup itself if it sits inside the folder.
    static IEnumerable<(FileInfo File, string Relative)> Files(string folder, string destination, List<string> unreadable, string into, ref int cloudOnly)
    {
        var found = new List<(FileInfo, string)>();
        string skipInside = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + "\\";
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(folder));
        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            string relativeDirectory = Path.GetRelativePath(folder, directory.FullName);
            FileSystemInfo[] entries;
            try
            {
                entries = directory.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                unreadable.Add(Path.Combine(into, relativeDirectory == "." ? "" : relativeDirectory).TrimEnd('\\'));
                continue;
            }
            foreach (FileSystemInfo entry in entries)
            {
                if (entry is DirectoryInfo sub)
                {
                    if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint) && !IsCloudFolder(sub)) continue;
                    if (SkippedFolders.Contains(sub.Name)) continue;
                    if ((sub.FullName + "\\").StartsWith(skipInside, StringComparison.OrdinalIgnoreCase)) continue;
                    pending.Push(sub);
                }
                else if (entry is FileInfo file)
                {
                    if (file.Name.StartsWith("~$", StringComparison.Ordinal) || file.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                        || file.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (IsCloudOnly(file)) cloudOnly++;
                    found.Add((file, Path.GetRelativePath(folder, file.FullName)));
                }
            }
        }
        return found;
    }

    static bool IsCloudOnly(FileInfo file) => (file.Attributes & CloudOnlyMarks) != 0;

    // OneDrive's folders are reparse points too, but real folders with the user's files: those are followed.
    static bool IsCloudFolder(DirectoryInfo folder) => (folder.Attributes & CloudOnlyMarks) != 0 || folder.LinkTarget is null;

    // The backup's copy of a file moves to Older versions\<time>, keeping its place inside the folder.
    static void MoveToOlder(string file, string current, string older)
    {
        string target = Path.Combine(older, Path.GetRelativePath(current, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(file, target, overwrite: true);
    }

    // Older versions folders past 30 days go, by the date in their name.
    static void PruneOlder(string olderRoot, DateTime now)
    {
        if (!Directory.Exists(olderRoot)) return;
        foreach (string folder in Directory.GetDirectories(olderRoot))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(folder), StampFormat, null, System.Globalization.DateTimeStyles.None, out DateTime when)
                || now - when <= KeepOlder)
                continue;
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Error("Couldn't remove an old backup version", ex);
            }
        }
    }

    static void RemoveEmptyFolders(string root)
    {
        try
        {
            foreach (string folder in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                         .OrderByDescending(folder => folder.Length).ToList())
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for next time.
        }
    }

    static void WriteReadMe(string destination)
    {
        string file = Path.Combine(destination, FolderName, "READ ME.txt");
        if (File.Exists(file)) return;
        File.WriteAllText(file,
            "Backups made by Assistant (https://github.com/ciberwarrior39-sys/Assistant).\r\n\r\n" +
            "Each computer has its own folder. Inside it:\r\n" +
            "- Current: the latest copy of your files, as normal files. Copy back whatever you need.\r\n" +
            "- Older versions: when a file was changed or deleted on the computer, its previous copy is kept here for 30 days,\r\n" +
            "  in a folder named by the date and time of that backup.\r\n\r\n" +
            "No app is needed to get your files back.\r\n");
    }

    static long FreeBytes(string destination)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1; // a network folder: unknown, so not checked
        }
    }

    static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleaned up next time.
        }
    }

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB" : $"{Math.Max(0, bytes) / 1024.0:0} KB";
}

/// <summary>
/// When the last backup ran and what it did, in backup-state.json in the data folder (separate from config.json, so the
/// Settings window's Cancel never puts an old date back).
/// </summary>
public sealed class BackupState
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public DateTime? LastBackupUtc { get; set; }
    public string LastSummary { get; set; } = "";
    public string LastNudge { get; set; } = "";   // the day the "no backup for a while" note last showed (yyyy-MM-dd)
    public bool Offered { get; set; }             // the one-time "your files aren't backed up" note was shown
    public DateTime? SetUpUtc { get; set; }       // when a destination was first chosen (the 7-day reminder counts from it)

    public static BackupState Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<BackupState>(File.ReadAllText(path), Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Couldn't save the backup state", ex);
        }
    }
}
