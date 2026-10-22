namespace Assistant.Core.Storage;

/// <summary>
/// Documents\Assistant: everything the assistant knows, in plain files the user can read, edit and delete.
/// </summary>
public sealed class DataFolder(string root)
{
    const string DefaultExclusions = """
        {
          "apps": ["KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe"],
          "windowTitlesContaining": ["InPrivate", "Incognito", "Private Browsing"]
        }
        """;

    public static DataFolder Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Assistant"));

    public string Root { get; } = root;
    public string MemoryFolder => Path.Combine(Root, "memory");
    public string ActivityFolder => Path.Combine(Root, "activity");
    public string FeedbackFolder => Path.Combine(Root, "feedback");
    public string SentFolder => Path.Combine(Root, "sent");
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string ExclusionsFile => Path.Combine(Root, "exclusions.json");
    public string ReviewFile => Path.Combine(Root, "review.json");

    /// <summary>"note: ..." — one note per line.</summary>
    public string NotesFile => Path.Combine(Root, "notes.md");

    /// <summary>The to-do list, as Markdown checkboxes ("- [ ] task").</summary>
    public string TodoFile => Path.Combine(Root, "todo.md");

    /// <summary>Timers and reminders waiting to go off, so they survive a restart.</summary>
    public string RemindersFile => Path.Combine(Root, "reminders.json");

    /// <summary>Custom characters, one folder each (Settings > Character > Create my own).</summary>
    public string CharactersFolder => Path.Combine(Root, "characters");

    /// <summary>Creates any missing folders and starter files. Never overwrites what the user has edited.</summary>
    public void EnsureCreated()
    {
        foreach (string folder in new[] { Root, MemoryFolder, ActivityFolder, FeedbackFolder, SentFolder, CharactersFolder })
            Directory.CreateDirectory(folder);

        if (!File.Exists(ConfigFile)) new AppConfig().Save(ConfigFile);
        WriteIfMissing(ExclusionsFile, DefaultExclusions);
        WriteIfMissing(Path.Combine(MemoryFolder, "about-me.md"),
            "# About me\n\nFacts the assistant has learned about you, one per line starting with \"- \". Edit or delete any line.\n");
        WriteIfMissing(Path.Combine(MemoryFolder, "preferences.md"),
            "# Preferences\n\nHow the assistant should behave, one per line starting with \"- \". Also sent with cloud chat.\n");
        WriteIfMissing(Path.Combine(MemoryFolder, "places.md"),
            "# Places\n\nFolders and files that matter to you, one per line starting with \"- \". Edit or delete any line.\n");
        WriteIfMissing(NotesFile, Organizer.NotesStore.Header);
        WriteIfMissing(TodoFile, Organizer.TodoStore.Header);
    }

    static void WriteIfMissing(string path, string content)
    {
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }
}
