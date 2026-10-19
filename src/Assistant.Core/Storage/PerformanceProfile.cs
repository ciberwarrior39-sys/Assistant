using Assistant.Core.Setup;

namespace Assistant.Core.Storage;

/// <summary>
/// How heavy the assistant is allowed to be. The privacy rules are the same in every profile: what's private is
/// handled on this computer, by the local model or by the app's own code; only plain chat may use the cloud.
/// </summary>
/// <param name="LocalModel">Run a local model at all. Lite answers clear requests with code only.</param>
/// <param name="ModelKeepAlive">How long the local model stays in memory after it was last used.</param>
/// <param name="SpeechModel">Whisper model for push-to-talk: base.en (better) or tiny.en (half the size, faster).</param>
/// <param name="StillWhenIdle">No bouncing while nothing is happening, so the character costs nothing then.</param>
public sealed record PerformanceProfile(
    string Id,
    string Label,
    string Summary,
    bool LocalModel,
    bool ScreenReading,
    TimeSpan ModelKeepAlive,
    string SpeechModel,
    int AnimationFps,
    bool StillWhenIdle)
{
    public static readonly PerformanceProfile Full = new("full", "Full",
        "Everything on. The local model stays ready for 5 minutes after each use. Best with a graphics card.",
        LocalModel: true, ScreenReading: true, TimeSpan.FromMinutes(5), "base.en", 30, StillWhenIdle: false);

    public static readonly PerformanceProfile Balanced = new("balanced", "Balanced",
        "A small local model that leaves memory after 2 minutes unused, and lighter speech recognition. For PCs without a graphics card.",
        LocalModel: true, ScreenReading: true, TimeSpan.FromMinutes(2), "tiny.en", 30, StillWhenIdle: false);

    public static readonly PerformanceProfile Lite = new("lite", "Lite",
        "No local model: clear requests (\"find my CV\", \"what did I do today?\", \"remember that…\") are answered by the app itself, " +
        "conversation uses the free cloud chat, and screen reading is off. For older or low-memory PCs.",
        LocalModel: false, ScreenReading: false, TimeSpan.Zero, "tiny.en", 15, StillWhenIdle: true);

    public static IReadOnlyList<PerformanceProfile> All { get; } = [Full, Balanced, Lite];

    public static PerformanceProfile For(string id) => All.FirstOrDefault(profile => profile.Id == id) ?? Full;

    /// <summary>
    /// From the hardware check: a graphics card that fits a model gets Full; 8 GB of memory or more (Windows reports
    /// a little less) gets Balanced with the tiny model on the processor; less than that gets Lite.
    /// </summary>
    public static PerformanceProfile Recommend(HardwarePlan plan) =>
        !plan.CloudRecommended ? Full
        : plan.RamGb is 0.0 or >= 7.5 ? Balanced // 0: the memory size couldn't be read
        : Lite;
}
