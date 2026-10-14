using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Memory;

/// <summary>
/// review.json in the data folder: when the last weekly review was, the one waiting for an answer,
/// and the facts the user chose to forget (so they aren't proposed again).
/// A review nobody answers within 2 days is dropped and nothing is saved: the default is forget.
/// </summary>
public sealed class ReviewSchedule(string path)
{
    public static readonly TimeSpan Every = TimeSpan.FromDays(7);
    public static readonly TimeSpan AnswerWithin = TimeSpan.FromDays(2);
    const int MaxDeclined = 50;

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    sealed class State
    {
        public DateTime? LastReview { get; set; }
        public DateTime? OfferedOn { get; set; }
        public List<ReviewCandidate> Pending { get; set; } = [];
        public List<string> Declined { get; set; } = [];
    }

    State state = Load(path);

    public IReadOnlyList<ReviewCandidate> Pending => state.Pending;

    /// <summary>Drops a review left unanswered too long. True if one was dropped.</summary>
    public bool Expire(DateTime today)
    {
        if (state.Pending.Count == 0 || state.OfferedOn is not { } offered || today - offered.Date < AnswerWithin) return false;

        DiagnosticLog.Info($"Weekly review unanswered for {AnswerWithin.TotalDays} days; nothing saved");
        Finish(today);
        return true;
    }

    /// <summary>
    /// The review to show: the one already waiting, or a new one when a week has passed.
    /// The first run only starts the clock, so the first review covers a full week of activity.
    /// With now = true (the tray menu's "Weekly review now"), it runs whatever the date.
    /// </summary>
    public IReadOnlyList<ReviewCandidate> StartIfDue(DateTime today, Func<IReadOnlyCollection<string>, IReadOnlyList<ReviewCandidate>> propose, bool now = false)
    {
        if (state.Pending.Count > 0) return state.Pending;

        if (!now)
        {
            if (state.LastReview is not { } last)
            {
                state.LastReview = today;
                Save();
                return [];
            }
            if (today - last.Date < Every) return [];
        }

        IReadOnlyList<ReviewCandidate> candidates = propose(state.Declined);
        if (candidates.Count == 0)
        {
            if (!now) Finish(today); // nothing worth asking about this week; a manual try doesn't move the clock
            return [];
        }

        state.Pending = [.. candidates];
        state.OfferedOn = today;
        Save();
        return state.Pending;
    }

    /// <summary>Records the user's answer. Forgotten facts are remembered only as "don't ask again".</summary>
    public void Decide(ReviewCandidate candidate, bool kept, DateTime today)
    {
        state.Pending.Remove(candidate);
        if (!kept)
        {
            state.Declined.Add(candidate.Fact);
            if (state.Declined.Count > MaxDeclined) state.Declined.RemoveRange(0, state.Declined.Count - MaxDeclined);
        }

        if (state.Pending.Count == 0) Finish(today);
        else Save();
    }

    void Finish(DateTime today)
    {
        state.Pending = [];
        state.OfferedOn = null;
        state.LastReview = today;
        Save();
    }

    static State Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<State>(File.ReadAllText(path), Options) ?? new State();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            DiagnosticLog.Error("review.json could not be read; starting the weekly review clock again", ex);
        }
        return new State();
    }

    void Save()
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(state, Options));
        }
        catch (IOException ex)
        {
            DiagnosticLog.Error("Could not write review.json", ex);
        }
    }
}
