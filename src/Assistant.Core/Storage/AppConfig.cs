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

    // Screensaver: the character bounces around the screen a few minutes before Windows turns the screen off
    // (Settings > Power > "Turn off my screen after"). If that is set to never, it starts after the fallback minutes.
    public bool ScreensaverEnabled { get; set; } = true;
    public int ScreensaverMinutesBeforeScreenOff { get; set; } = 3;
    public int ScreensaverFallbackMinutes { get; set; } = 10;

    // Updates: once a day the installed app looks for a new version on the project's GitHub Releases,
    // downloads it quietly and installs it on the next start.
    public bool AutoUpdate { get; set; } = true;

    // A gentle "time for a break?" after this many minutes of using the PC without a 5-minute pause; 0 turns it off.
    public int BreakReminderMinutes { get; set; } = 60;

    // The day the birthday greeting was last shown (yyyy-MM-dd), so it comes once.
    public string LastBirthdayGreeting { get; set; } = "";

    // "search the web for …": google, duckduckgo or bing. The query opens in the default browser.
    public string SearchEngine { get; set; } = "google";

    // Start with Windows (installed app only): a Run entry for the current user, also switchable in Task Manager.
    public bool StartWithWindows { get; set; } = true;

    // The character's colors and size, and the chat bubble's theme. Easiest to change in Settings.
    public Appearance Appearance { get; set; } = new();

    public static AppConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                AppConfig config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options) ?? new AppConfig();
                config.Appearance ??= new Appearance();
                config.Appearance.Repair();
                config.Upgrade();
                // Writes back any settings added in newer versions, keeping the user's values.
                try
                {
                    config.Save(path);
                }
                catch (IOException ex)
                {
                    DiagnosticLog.Error("Could not update config.json", ex);
                }
                return config;
            }
        }
        catch (JsonException ex)
        {
            // Left untouched so the user can fix their edit.
            DiagnosticLog.Error("config.json is not valid JSON; using defaults", ex);
        }
        return new AppConfig();
    }

    /// <summary>Old defaults that were never changed by hand move to the new ones (the file stores every value).</summary>
    void Upgrade()
    {
        if (GroqModel == "llama-3.3-70b-versatile") GroqModel = "openai/gpt-oss-120b"; // 0.4.1: a much stronger model
        if (ContextSize == 2048) ContextSize = 4096;                                      // 0.4.1: room for picked facts
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options));

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
