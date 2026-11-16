using System.Text.Json;
using System.Text.Json.Serialization;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Storage;

/// <summary>
/// Settings from config.json in the data folder. The Settings window edits most of them;
/// after editing the file by hand, restart the app.
/// </summary>
public sealed class AppConfig
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // Set by the setup wizard. Until it has run, the wizard opens at startup.
    public bool SetupDone { get; set; }

    // The local brain. Embedded: the app's own copy of Ollama, started by the app on its own port
    // (%LocalAppData%\Assistant\ollama). Otherwise an Ollama the user installed, at ollamaUrl.
    public bool OllamaEmbedded { get; set; }
    public string OllamaUrl { get; set; } = "http://localhost:11434/";
    public string Model { get; set; } = "qwen3.5:2b";
    public int ContextSize { get; set; } = 4096; // room for the conversation plus the facts picked for each message

    // full, balanced or lite (PerformanceProfile): how heavy the assistant may be. The setup wizard picks it from the
    // hardware; Settings > Performance changes it (after a restart).
    public string Performance { get; set; } = "full";

    // Set when the user switched to Lite and agreed to free the space: the app's own Ollama and model are deleted at the
    // next start (Lite never starts them). "Run setup again" downloads them back.
    public bool RemoveLocalModel { get; set; }

    // Optional free cloud models for conversation. Groq is recommended; OpenRouter signs up without a phone.
    // Leave a key empty to skip that provider. Without any key, everything runs locally.
    // Keys are saved encrypted for this Windows user ("dpapi:..."); a key pasted here as plain text is encrypted on the next save.
    [JsonIgnore] public string GroqApiKey { get; set; } = "";
    public string GroqModel { get; set; } = "openai/gpt-oss-120b";
    [JsonIgnore] public string OpenRouterApiKey { get; set; } = "";
    public string OpenRouterModel { get; set; } = "openrouter/free";

    // Cloud chat may also get the few about-me facts that matter for the message (never places, files, notes,
    // activity or past conversations). Off unless the user turns it on in Settings > Privacy and cloud.
    public bool CloudKnowsAboutMe { get; set; }

    // Custom shortcuts: a key combination runs a command, as if it were typed to the assistant ("Alt+S" → "open spotify").
    // Settings > Shortcuts, or "make Alt+S open Spotify" in the chat. Paused while a game runs, so games get the keys.
    public List<Shortcut> Shortcuts { get; set; } = [];
    public bool ShortcutsPauseInGames { get; set; } = true;

    // Groq's cloud model may search the web (for facts it isn't sure of) and run Python (for exact sums) while it answers.
    // The searches go through Groq's search partner; everything sent is still logged in the sent folder.
    public bool CloudWebSearch { get; set; } = true;

    // Calendar and mail (Settings > Calendar and mail). The calendar links are keys to the calendar and the password
    // opens the inbox, so all of it is saved encrypted for this Windows user, like the cloud keys.
    // calendarLinks: private iCal (.ics) links, one per line. mailAddress + mailPassword: an app password, read-only
    // use over IMAP. mailServer: only when the provider's server can't be guessed from the address.
    [JsonIgnore] public string CalendarLinks { get; set; } = "";
    public string MailAddress { get; set; } = "";
    [JsonIgnore] public string MailPassword { get; set; } = "";
    public string MailServer { get; set; } = "";

    [JsonPropertyName("calendarLinks")]
    public string CalendarLinksStored
    {
        get => Secrets.Protect(CalendarLinks);
        set => CalendarLinks = Secrets.Unprotect(value);
    }

    [JsonPropertyName("mailPassword")]
    public string MailPasswordStored
    {
        get => Secrets.Protect(MailPassword);
        set => MailPassword = Secrets.Unprotect(value);
    }

    [JsonPropertyName("groqApiKey")]
    public string GroqApiKeyStored
    {
        get => Secrets.Protect(GroqApiKey);
        set => GroqApiKey = Secrets.Unprotect(value);
    }

    [JsonPropertyName("openRouterApiKey")]
    public string OpenRouterApiKeyStored
    {
        get => Secrets.Protect(OpenRouterApiKey);
        set => OpenRouterApiKey = Secrets.Unprotect(value);
    }

    // Activity log: which apps and window titles you use, kept 7 days in the activity folder, never sent anywhere.
    public bool ActivityLogging { get; set; } = true;

    // Game mode: when a fullscreen game runs, free the GPU and hide the character.
    // Add a game's .exe name (for example "Game.exe") if it isn't detected automatically.
    public bool GameModeEnabled { get; set; } = true;
    public string[] GameExecutables { get; set; } = [];

    // Close while I play Riot games: quit the moment the Riot Client, League or Valorant opens, so nothing of the
    // assistant runs next to Riot Vanguard. It doesn't start while one is open, and doesn't come back by itself.
    public bool QuitForRiotGames { get; set; } = false;

    // Hand-raise: a silent "!" when you seem stuck. The numbers grow by one after 3 ignored hand-raises in a row.
    // searchLoopTrips: back-and-forths between an app and search pages within 5 minutes.
    // stuckSwitches: window switches within 3 minutes before sitting still in one window.
    public bool HandRaiseEnabled { get; set; } = true;
    public int SearchLoopTrips { get; set; } = 3;
    public int StuckSwitches { get; set; } = 10;

    // Voice: hold the push-to-talk keys while talking (no always-on microphone). Replies to spoken questions are
    // read aloud: voiceName is "piper:<id>" for a natural voice (Settings > Voice downloads it), part of a Windows
    // voice's name (e.g. "Zira"), or empty for Windows' default. voiceRate is -10 to 10.
    public bool VoiceEnabled { get; set; } = true;
    public string PushToTalkKey { get; set; } = "Ctrl+Alt+Space";
    public string VoiceName { get; set; } = "";
    public int VoiceRate { get; set; } = 0;

    // The language spoken to it: "en", "bg", or "auto" for both. Anything but English downloads a bigger speech
    // model (about 466 MB) the first time voice is used.
    public string SpeechLanguage { get; set; } = "en";

    // Wake word: off unless turned on. With it, the microphone stays open and the phrase ("Hey Assistant") starts a
    // spoken question without the keys. Heard on this computer only; closed during games, a locked PC and calls.
    public bool WakeWordEnabled { get; set; }

    // How sure the wake word has to be, 0.2 to 0.9. Lower hears a quiet or accented phrase more easily and wakes by
    // mistake more often.
    public double WakeWordThreshold { get; set; } = 0.5;

    // Screensaver: the character bounces around the screen a few minutes before Windows turns the screen off
    // (Settings > Power > "Turn off my screen after"). If that is set to never, it starts after the fallback minutes.
    public bool ScreensaverEnabled { get; set; } = true;
    public int ScreensaverMinutesBeforeScreenOff { get; set; } = 3;
    public int ScreensaverFallbackMinutes { get; set; } = 10;

    // Updates from the project's GitHub Releases, looked for once a day by the installed app:
    // "auto" downloads a new version quietly and installs it on the next start; "notify" only says one is out and
    // installs it when asked; "off" doesn't look (Settings > Startup and updates > Check now still works).
    public string UpdateMode { get; set; } = "auto";

    /// <summary>Settings before 0.6.0 had a yes/no "autoUpdate": no becomes "off". Read once, never written again.</summary>
    [JsonPropertyName("autoUpdate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? OldAutoUpdate
    {
        get => null;
        set
        {
            if (value == false) UpdateMode = "off";
        }
    }

    // The version that last ran, so the first start after an update can say what's new (Settings > What's new).
    public string LastSeenVersion { get; set; } = "";

    // A gentle "time for a break?" after this many minutes of using the PC without a 5-minute pause; 0 turns it off.
    public int BreakReminderMinutes { get; set; } = 60;

    // The day the birthday greeting was last shown (yyyy-MM-dd), so it comes once.
    public string LastBirthdayGreeting { get; set; } = "";

    // "search the web for …": google, duckduckgo or bing. The query opens in the default browser.
    public string SearchEngine { get; set; } = "google";

    // Backups (Settings > Backups, or "back up my files" in the chat): copies of these folders go to the destination as
    // plain files, Assistant Backup\<PC>\Current, with older versions of changed or deleted files kept 30 days.
    // backupFolders empty means Documents, Desktop and Pictures. backupId recognises the drive if its letter changes.
    public string BackupDestination { get; set; } = "";
    public string BackupId { get; set; } = "";
    public List<string> BackupFolders { get; set; } = [];
    public bool BackupAuto { get; set; } = true; // once a day, when the destination is there

    // Start with Windows (installed app only): a Run entry for the current user, also switchable in Task Manager.
    public bool StartWithWindows { get; set; } = true;

    // The character's colors and size, and the chat bubble's theme. Easiest to change in Settings.
    public Appearance Appearance { get; set; } = new();

    public static AppConfig Load(string path)
    {
        if (Read(path) is { } config)
        {
            // The file as it was read, kept as the last copy known to be good.
            try
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Error("Could not keep a spare copy of config.json", ex);
            }
            // Writes back any settings added in newer versions, keeping the user's values.
            config.Save(path);
            return config;
        }

        if (!File.Exists(path)) return new AppConfig();

        // config.json is there but can't be used (a hand edit gone wrong, or cut off by a crash). It is set aside as
        // config.broken.json, so a later save can't destroy it, and the last good copy takes its place if there is one.
        try
        {
            File.Copy(path, Path.Combine(Path.GetDirectoryName(path) ?? "", "config.broken.json"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Could not set the unreadable config.json aside", ex);
        }
        if (Read(path + ".bak") is { } spare)
        {
            DiagnosticLog.Info("config.json could not be read; using the last good copy (the unreadable one is config.broken.json)");
            spare.Save(path);
            return spare;
        }
        DiagnosticLog.Info("config.json could not be read and there is no spare copy; using defaults");
        return new AppConfig();
    }

    // The settings in a file, repaired and upgraded; null when it's missing, unreadable or not valid JSON.
    static AppConfig? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            AppConfig? config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options);
            if (config is null) return null;
            config.Appearance ??= new Appearance();
            config.Appearance.Repair();
            config.Upgrade();
            return config;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DiagnosticLog.Error("A settings file could not be read", ex);
            return null;
        }
    }

    /// <summary>Old defaults that were never changed by hand move to the new ones (the file stores every value).</summary>
    void Upgrade()
    {
        if (GroqModel == "llama-3.3-70b-versatile") GroqModel = "openai/gpt-oss-120b"; // 0.4.1: a much stronger model
        if (ContextSize == 2048) ContextSize = 4096;                                      // 0.4.1: room for picked facts
        if (UpdateMode is not ("auto" or "notify" or "off")) UpdateMode = "auto";
        Shortcuts ??= [];
        BackupFolders ??= [];
        BackupDestination ??= "";
        BackupId ??= "";
        Shortcuts.RemoveAll(shortcut => shortcut is null || string.IsNullOrWhiteSpace(shortcut.Keys));
    }

    /// <summary>
    /// Saves the settings without ever leaving half a file (SafeFile). Never throws: a failed save is logged and the
    /// settings stay as they are in memory, to be saved the next time. False when it didn't work.
    /// </summary>
    public bool Save(string path)
    {
        try
        {
            SafeFile.WriteAllText(path, JsonSerializer.Serialize(this, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Could not save config.json", ex);
            return false;
        }
    }

    /// <summary>A separate copy, so the Settings window's Cancel can put everything back.</summary>
    public AppConfig Copy() => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, Options), Options)!;

    /// <summary>Copies every setting from another config into this one (the app keeps one shared instance).</summary>
    public void CopyFrom(AppConfig other)
    {
        foreach (var property in typeof(AppConfig).GetProperties().Where(property => property.CanWrite))
            property.SetValue(this, property.GetValue(other));
        Appearance = other.Appearance.Copy();
    }
}
