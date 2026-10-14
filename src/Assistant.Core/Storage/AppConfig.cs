using System.Text.Json;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Storage;

/// <summary>Settings from config.json in the data folder. Edit the file and restart the app.</summary>
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

    public static AppConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                AppConfig config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options) ?? new AppConfig();
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
}
