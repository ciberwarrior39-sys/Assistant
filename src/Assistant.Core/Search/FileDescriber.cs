namespace Assistant.Core.Search;

/// <summary>
/// What a found file or folder is, in a line, from the disk: "a C# project (daskalo.csproj) with 12 files", "a plain
/// folder with 3 files, like Proba.accdb", "an Access database, 1.2 MB". For "which one is just a folder?" after a
/// search. Only names and counts are read, never what is inside the files, and none of it leaves the computer.
/// </summary>
public static class FileDescriber
{
    /// <summary>One item: the line, and whether it is a plain folder (no project or repository in it).</summary>
    public sealed record Description(string Text, bool PlainFolder);

    const int MaxEntries = 5000; // enough to count a normal folder; a huge one is "over 5000 items"

    static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// The whole list, numbered like the cards ("1. …"), with a first line that answers "which one is just a folder?"
    /// when <paramref name="plainAsked"/>.
    /// </summary>
    public static string DescribeList(IReadOnlyList<FileHit> hits, bool plainAsked)
    {
        List<Description> described = hits.Select(Describe).ToList();
        var lines = new List<string>();
        if (plainAsked)
        {
            List<int> plain = described.Select((item, index) => (item, index)).Where(pair => pair.item.PlainFolder).Select(pair => pair.index + 1).ToList();
            lines.Add(plain.Count switch
            {
                0 => "None of them is just a plain folder:",
                _ when plain.Count == hits.Count => hits.Count == 1 ? "It's just a plain folder:" : "All of them are plain folders:",
                1 => $"Number {plain[0]} is just a plain folder:",
                _ => $"Numbers {string.Join(" and ", plain)} are just plain folders:",
            });
        }
        else
        {
            lines.Add(hits.Count == 1 ? "Here's what it is:" : "Here's what each one is:");
        }

        for (int i = 0; i < hits.Count; i++)
            lines.Add($"{i + 1}. {Name(hits[i])} ({Where(hits[i])}): {described[i].Text}.");
        return string.Join("\n", lines);
    }

    public static Description Describe(FileHit hit)
    {
        try
        {
            if (Directory.Exists(hit.FullPath)) return DescribeFolder(hit.FullPath);
            if (File.Exists(hit.FullPath)) return new Description(DescribeFile(hit.FullPath), false);
            return new Description("isn't there anymore", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new Description("I couldn't look inside it", false);
        }
    }

    static Description DescribeFolder(string path)
    {
        var fileNames = new List<string>();
        var folderNames = new List<string>();
        int seen = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (++seen > MaxEntries) break;
            if (Directory.Exists(entry)) folderNames.Add(Path.GetFileName(entry));
            else fileNames.Add(Path.GetFileName(entry));
        }

        string contents = seen > MaxEntries ? $"over {MaxEntries} items" : Contents(fileNames.Count, folderNames.Count);
        if (seen == 0) return new Description("an empty folder", true);

        bool Has(string name) => fileNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        bool HasFolder(string name) => folderNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        string? With(string extension) => fileNames.FirstOrDefault(name => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

        string? kind =
            With(".sln") is { } solution ? $"a Visual Studio solution ({solution})"
            : With(".csproj") is { } csharp ? $"a C# project ({csharp})"
            : With(".vcxproj") is { } cpp ? $"a C++ project ({cpp})"
            : With(".fsproj") is { } fsharp ? $"an F# project ({fsharp})"
            : With(".vbproj") is { } vb ? $"a Visual Basic project ({vb})"
            : HasFolder("Assets") && HasFolder("ProjectSettings") ? "a Unity project"
            : Has("package.json") ? "a Node.js project"
            : Has("pyproject.toml") || Has("requirements.txt") || Has("setup.py") ? "a Python project"
            : Has("pom.xml") || Has("build.gradle") || Has("build.gradle.kts") ? "a Java project"
            : Has("Cargo.toml") ? "a Rust project"
            : Has("go.mod") ? "a Go project"
            : null;
        bool git = HasFolder(".git");

        if (kind is not null)
            return new Description($"{kind}{(git ? " in a Git repository" : "")}, {contents}", false);
        if (git)
            return new Description($"a Git repository, {contents}", false);

        // A plain folder: a couple of its files by name, so the user can tell which one it is.
        string examples = fileNames.Count == 0 ? "" : $", like {string.Join(" and ", fileNames.Take(2))}";
        return new Description($"a plain folder with {contents}{examples}", true);
    }

    static string Contents(int files, int folders) => (files, folders) switch
    {
        (_, 0) => Plural(files, "file"),
        (0, _) => Plural(folders, "folder"),
        _ => $"{Plural(files, "file")} and {Plural(folders, "folder")}",
    };

    static string Plural(int count, string word) => count == 1 ? $"1 {word}" : $"{count} {word}s";

    static string DescribeFile(string path)
    {
        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        string kind = Kinds.TryGetValue(extension, out string? known) ? known
                    : extension.Length == 0 ? "a file with no type" : $"a .{extension} file";
        return $"{kind}, {Size(new FileInfo(path).Length)}";
    }

    static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["accdb"] = "an Access database", ["mdb"] = "an Access database",
        ["doc"] = "a Word document", ["docx"] = "a Word document", ["odt"] = "a text document", ["rtf"] = "a text document",
        ["xls"] = "an Excel workbook", ["xlsx"] = "an Excel workbook", ["csv"] = "a table (CSV)",
        ["ppt"] = "a PowerPoint presentation", ["pptx"] = "a PowerPoint presentation",
        ["pdf"] = "a PDF", ["txt"] = "a text file", ["md"] = "a text file (Markdown)",
        ["png"] = "an image", ["jpg"] = "an image", ["jpeg"] = "an image", ["gif"] = "an image", ["webp"] = "an image", ["heic"] = "a photo",
        ["mp3"] = "a song or audio file", ["wav"] = "an audio file", ["flac"] = "an audio file", ["m4a"] = "an audio file",
        ["mp4"] = "a video", ["mkv"] = "a video", ["mov"] = "a video", ["avi"] = "a video",
        ["zip"] = "a zip archive", ["rar"] = "a RAR archive", ["7z"] = "a 7-Zip archive",
        ["exe"] = "a program", ["msi"] = "an installer", ["lnk"] = "a shortcut",
        ["sln"] = "a Visual Studio solution file", ["csproj"] = "a C# project file", ["cs"] = "C# code", ["py"] = "Python code",
        ["js"] = "JavaScript code", ["html"] = "a web page", ["json"] = "a JSON file", ["unity"] = "a Unity scene",
    };

    /// <summary>
    /// A button's label for one of several: its name, and where it is when another in the list has the same name
    /// ("daskalo (Desktop)", "daskalo (…\repos\daskalo)").
    /// </summary>
    public static string Label(FileHit hit, IReadOnlyList<FileHit> all)
    {
        string name = Name(hit);
        if (all.Count(other => Name(other).Equals(name, StringComparison.OrdinalIgnoreCase)) < 2) return name;
        string[] folders = Where(hit).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string place = folders.Length <= 2 ? string.Join("\\", folders) : "…\\" + string.Join("\\", folders[^2..]);
        return $"{name} ({place})";
    }

    static string Name(FileHit hit) => Path.GetFileName(hit.FullPath.TrimEnd('\\')) is { Length: > 0 } name ? name : hit.FullPath;

    // Where it is, short: "Desktop", "source\repos\daskalo"; outside the user's folder, the full folder path.
    static string Where(FileHit hit)
    {
        string folder = Path.GetDirectoryName(hit.FullPath.TrimEnd('\\')) ?? hit.FullPath;
        if (folder.StartsWith(UserProfile + "\\", StringComparison.OrdinalIgnoreCase)) return folder[(UserProfile.Length + 1)..];
        return folder.Equals(UserProfile, StringComparison.OrdinalIgnoreCase) ? "your user folder" : folder;
    }
}
