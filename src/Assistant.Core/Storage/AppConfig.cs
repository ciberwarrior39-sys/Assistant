using System.Text.Json;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Storage;

/// <summary>
/// Settings from config.json in the data folder. The Settings window edits most of them;
/// after editing the file by hand, restart the app.
/// </summary>
public sealed class AppConfig
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string OllamaUrl { get; set; } = "http://localhost:11434/";
    public string Model { get; set; } = "qwen3.5:2b";
    public int ContextSize { get; set; } = 2048;

    // Optional free cloud models for conversation. Groq is recommended; OpenRouter signs up without a phone.
    // Leave a key empty to skip that provider. Without any key, everything runs locally.
    public string GroqApiKey { get; set; } = "";
    public string GroqModel { get; set; } = "llama-3.3-70b-versatile";
    public string OpenRouterApiKey { get; set; } = "";
    public string OpenRouterModel { get; set; } = "openrouter/free";

    // Activity log: which apps and window titles you use, kept 7 days in the activity folder, never sent anywhere.
    public bool ActivityLogging { get; set; } = true;

    // Game mode: when a fullscreen game runs, free the GPU and hide the character.
    // Add a game's .exe name (for example "Game.exe") if it isn't detected automatically.
    public bool GameModeEnabled { get; set; } = true;
    public string[] GameExecutables { get; set; } = [];

    // Hand-raise: a silent "!" when you seem stuck. The numbers grow by one after 3 ignored hand-raises in a row.
    // searchLoopTrips: back-and-forths between an app and search pages within 5 minutes.
    // stuckSwitches: window switches within 3 minutes before sitting still in one window.
    public bool HandRaiseEnabled { get; set; } = true;
    public int SearchLoopTrips { get; set; } = 3;
    public int StuckSwitches { get; set; } = 10;

    // Voice: hold the push-to-talk keys while talking (no always-on microphone). Replies to spoken questions are
    // read aloud with a Windows voice; voiceName picks one by part of its name (e.g. "Zira"), voiceRate is -10 to 10.
    public bool VoiceEnabled { get; set; } = true;
    public string PushToTalkKey { get; set; } = "Ctrl+Alt+Space";
    public string VoiceName { get; set; } = "";
    public int VoiceRate { get; set; } = 0;

    // Screensaver: the character bounces around the screen a few minutes before Windows turns the screen off
    // (Settings > Power > "Turn off my screen after"). If that is set to never, it starts after the fallback minutes.
    public bool ScreensaverEnabled { get; set; } = true;
    public int ScreensaverMinutesBeforeScreenOff { get; set; } = 3;
    public int ScreensaverFallbackMinutes { get; set; } = 10;

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
