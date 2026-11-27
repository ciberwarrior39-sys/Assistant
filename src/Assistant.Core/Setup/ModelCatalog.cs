namespace Assistant.Core.Setup;

/// <summary>A local model the app offers: its Ollama name, download size, and short name ("2B").</summary>
public sealed record LocalModelInfo(string Name, double DownloadGb, string Short);

/// <summary>How long a model took for a typical answer on this PC (<see cref="OllamaRuntime.TimeAnswerAsync"/>).</summary>
public sealed record ModelTiming(bool Answered, TimeSpan Time);

/// <summary>
/// The local models, smallest first, and the rule for trying a bigger one: it's downloaded, timed on this PC on the
/// same kind of request the app sends most (rereading a follow-up), and kept only if that takes at most
/// <see cref="FastEnough"/>. Sizes are Ollama's downloads (model plus its picture reader).
/// </summary>
public static class ModelCatalog
{
    public const string Tiny = "qwen3.5:0.8b";
    public const string Small = "qwen3.5:2b";
    public const string Big = "qwen3.5:4b";

    public static readonly IReadOnlyList<LocalModelInfo> All =
    [
        new(Tiny, 1.3, "0.8B"),
        new(Small, 2.7, "2B"),
        new(Big, 4.0, "4B"),
    ];

    /// <summary>
    /// The longest a typical answer may take for the bigger model to be kept. The follow-up step runs on every message
    /// the rules miss, so much more than this makes the whole assistant feel slow.
    /// </summary>
    public static readonly TimeSpan FastEnough = TimeSpan.FromSeconds(3);

    public static LocalModelInfo? Find(string name) =>
        All.FirstOrDefault(model => model.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The bigger model to offer someone who has <paramref name="current"/>: what the hardware plan suggests or would
    /// try, when it's bigger than theirs and wasn't offered before. Null for a model of their own (not in the list).
    /// </summary>
    public static LocalModelInfo? Upgrade(HardwarePlan plan, string current, IReadOnlyCollection<string> alreadyOffered)
    {
        if (Find(current) is not { } have || Find(plan.TryModel ?? plan.Model) is not { } fits) return null;
        if (Index(fits) <= Index(have)) return null;
        return alreadyOffered.Contains(fits.Name, StringComparer.OrdinalIgnoreCase) ? null : fits;
    }

    /// <summary>Keep the bigger model only if it answered, and quickly enough.</summary>
    public static bool KeepBigger(ModelTiming bigger) => bigger.Answered && bigger.Time <= FastEnough;

    static int Index(LocalModelInfo model) => All.ToList().IndexOf(model);
}
