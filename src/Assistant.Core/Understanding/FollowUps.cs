using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Core.Llm;
using Assistant.Core.Search;

namespace Assistant.Core.Understanding;

/// <summary>
/// One exchange, as the follow-up step needs it: what was asked, which part of the app answered, what it said, and the
/// files it showed (<see cref="Focus"/>: the one it opened, which "it" means next).
/// </summary>
public sealed record Turn(string Asked, string Route, string Answer, DateTime At)
{
    public IReadOnlyList<FileHit> Files { get; init; } = [];
    public FileHit? Focus { get; init; }

    /// <summary>The file search this answer ran, if any ("a folder, perhaps?" looks again for folders).</summary>
    public FileSearchQuery? Search { get; init; }

    /// <summary>
    /// Set when this answer asked "Which one should I open/show?": what to do with the one named next (true: show it
    /// in its folder). Null when nothing was asked.
    /// </summary>
    public bool? PendingReveal { get; init; }
}

/// <summary>
/// The last few exchanges, kept in memory on this computer for a few minutes so a follow-up ("and tomorrow?", "did he
/// reply?", "open the second one") can be understood. Private answers are included: this never leaves the computer
/// and is never written to disk or to the log. It is forgotten after <see cref="KeepFor"/> without a message.
/// </summary>
public sealed class ConversationMemory
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);
    const int MaxTurns = 4;
    const int MaxAnswerChars = 300;

    readonly List<Turn> turns = [];

    public void Add(string asked, string route, string answer, DateTime now, IReadOnlyList<FileHit>? files = null, FileHit? focus = null,
                    FileSearchQuery? search = null, bool? pendingReveal = null)
    {
        Forget(now);
        string shortAnswer = answer.Length > MaxAnswerChars ? answer[..MaxAnswerChars] + "…" : answer;
        turns.Add(new Turn(asked, route, shortAnswer, now) { Files = files ?? [], Focus = focus, Search = search, PendingReveal = pendingReveal });
        if (turns.Count > MaxTurns) turns.RemoveAt(0);
    }

    public IReadOnlyList<Turn> Recent(DateTime now)
    {
        Forget(now);
        return turns.ToList();
    }

    public void Clear() => turns.Clear();

    void Forget(DateTime now)
    {
        if (turns.Count > 0 && now - turns[^1].At > KeepFor) turns.Clear();
    }
}

/// <summary>What a message means once the conversation is taken into account, and which part of the app it is for.</summary>
public sealed record Rewrite(string Request, string Feature);

/// <summary>
/// The step between the rules and the chat models: the local model reads the recent conversation and the message the
/// rules didn't recognise, and rewrites it into one complete request in plain words ("and tomorrow?" after a calendar
/// question becomes "what's on my calendar tomorrow"). The app then routes that request through the same rules.
/// The model only translates; it never answers, and nothing here goes to the cloud.
/// </summary>
public static partial class FollowUps
{
    /// <summary>What the model may pick; "chat" means "leave the message as it is".</summary>
    public static readonly IReadOnlyList<string> Features =
        ["calendar", "mail", "timer", "reminder", "todo", "note", "routine", "action", "files", "guide", "chat"];

    const string Prompt = """
        You help a desktop assistant understand the user's latest message. Rewrite it into ONE complete request that makes
        sense on its own, using the recent conversation to fill in what it refers to ("it", "he", "that one", "and
        tomorrow?", "the second one"). Use the plainest wording, in the user's language, like these:
          "what's on my calendar tomorrow"   "any new emails"   "did Ivan reply"   "any new work emails"   "my latest email"
          "set a timer for 5 minutes"   "remind me at 18:00 to call mum"   "add milk to my list"   "open spotify"
          "volume up"   "find my CV"   "where is the save button"
        Keep names, dates, times, numbers and mailbox names the conversation gives, but the latest message wins: if it
        names a new person, sender, day or thing, use that one instead of the earlier one. For example, after "any
        emails from GitHub?", "how about chat gpt?" means "any emails from chat gpt", and a bare name right after a mail
        question ("Discord?") means mail from that sender. Do not answer the message.
        Then pick the part of the app it is for: calendar, mail, timer, reminder, todo, note, routine, action (open an
        app or site, volume, music, settings, screenshot, lock, theme, check the PC or internet), files, guide (where to
        click in an app), or chat (everything else: conversation, questions, writing, facts).
        If it is chat, give the message back unchanged.
        Answer with JSON only, nothing else: {"request": "...", "feature": "..."}
        """;

    /// <summary>
    /// A message that probably leans on what came before: short, or starting with "and"/"what about", or pointing at
    /// something ("it", "him", "that one", "the second"), in English or Bulgarian.
    /// </summary>
    public static bool LooksLikeFollowUp(string message)
    {
        string text = message.Trim();
        if (text.Length == 0) return false;
        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return words <= 4 || Leaning().IsMatch(text);
    }

    /// <summary>
    /// The plainest follow-ups, worked out by code before any model is asked: right after a mail or calendar answer, a
    /// bare name or day asked as a question, or led by "and"/"what about"/"from" ("Discord?", "from soft uni?", "and
    /// Friday?"), means the same question again about that. Returns the request to try; the caller checks a rule
    /// accepts it. Null for anything else, including "thanks?" and real questions ("what is Discord?").
    /// </summary>
    public static Rewrite? Carry(string message, IReadOnlyList<Turn> recent)
    {
        if (recent.Count == 0) return null;
        string last = recent[^1].Route;
        string? feature = last.EndsWith("mail", StringComparison.Ordinal) ? "mail"
                        : last.EndsWith("calendar", StringComparison.Ordinal) ? "calendar"
                        : null;
        if (feature is null) return null;

        string text = message.Trim();
        bool asked = text.EndsWith('?');
        text = text.TrimEnd('?', '!', '.', ' ');
        Match lead = Lead().Match(text);
        if (lead.Success) text = text[lead.Length..].Trim();
        if (!asked && !lead.Success) return null;

        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words is 0 or > 3 || NotAThing().IsMatch(text)) return null;
        return feature == "mail"
            ? new Rewrite($"any emails from {text}", "mail")
            : new Rewrite($"what's on my calendar {text}", "calendar");
    }

    /// <summary>
    /// The small model sometimes falls back on its own examples ("A folder, perhaps?" after a file search came back as
    /// the calendar). A rewrite into the calendar, the inbox or "where to click" is kept only when the conversation was
    /// just about it, or the message itself mentions it (a day, a meeting, mail, a reply, a button, a window).
    /// </summary>
    public static bool Plausible(Rewrite rewrite, string message, IReadOnlyList<Turn> recent) => rewrite.Feature switch
    {
        "calendar" => recent.Any(turn => turn.Route.EndsWith("calendar", StringComparison.Ordinal)) || CalendarHint().IsMatch(message),
        "mail" => recent.Any(turn => turn.Route.EndsWith("mail", StringComparison.Ordinal)) || MailHint().IsMatch(message),
        "guide" => recent.Any(turn => turn.Route.EndsWith("guide", StringComparison.Ordinal)) || GuideHint().IsMatch(message),
        _ => true,
    };

    [GeneratedRegex(@"(?<!\p{L})(?:calendar|schedule|agenda|event|meeting|appointment|busy|free|plans?|today|tonight|tomorrow|yesterday|week|weekend|monday|tuesday|wednesday|thursday|friday|saturday|sunday|morning|afternoon|evening|календар|събити|срещ|ангажимент|зает|свобод|днес|довечера|утре|вчера|седмиц|уикенд|понеделник|вторник|сряда|четвъртък|петък|събота|неделя|сутрин|следобед|вечер)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CalendarHint();

    [GeneratedRegex(@"(?<!\p{L})(?:mail|email|e-mail|inbox|repl|wrote|written|sent|send|message|letter|newsletter|поща|имейл|мейл|писм|отговор|писа|прати|прачи|съобщени)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MailHint();

    [GeneratedRegex(@"(?<!\p{L})(?:click|press|tap|button|menu|tab|icon|option|setting|toggle|checkbox|window|app|program|screen|кликн|натисн|цъкн|бутон|меню|опци|настройк|прозор|приложение|програм|екран)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GuideHint();

    [GeneratedRegex(@"^(?:and\s+from|and\s+for|and\s+on|and|what\s+about|how\s+about|from|а\s+от|ами\s+от|а|ами|и|от)\s+",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Lead();

    // A question word, a thank-you, a yes/no or "my" ("and on my work email?" is for the model): not a name or a day.
    [GeneratedRegex(@"(?<![\p{L}])(?:what|who|why|how|when|where|which|is|are|can|could|does|do|did|thanks?|thank\s+you|ok|okay|cool|nice|great|yes|no|sure|really|my|mine|your|моя|моята|моите|ми|какво|кой|коя|кое|защо|как|кога|къде|е|ли|благодаря|мерси|добре|да|не)(?![\p{L}])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotAThing();

    /// <summary>The model's reading of the message, or null when it gave nothing usable.</summary>
    public static async Task<Rewrite?> RewriteAsync(ILlmProvider model, string message, IReadOnlyList<Turn> recent, CancellationToken ct)
    {
        var context = new StringBuilder();
        if (recent.Count > 0)
        {
            context.Append("Recent conversation:\n");
            foreach (Turn turn in recent)
                context.Append($"User: {OneLine(turn.Asked)}\nAssistant ({turn.Route}): {OneLine(turn.Answer)}\n");
            context.Append('\n');
        }
        context.Append($"Latest message: {message.Trim()}");

        string answer;
        try
        {
            answer = (await model.ChatAsync([ChatMessage.ForSystem(Prompt), ChatMessage.ForUser(context.ToString())], [], ct)).Content;
        }
        catch (Exception ex) when (ex is HttpRequestException or LlmException or TaskCanceledException)
        {
            return null; // no local model right now: the message goes on as it was
        }
        return Read(answer);
    }

    /// <summary>The JSON in a model's answer (a small model may wrap it in words or a code fence), checked.</summary>
    public static Rewrite? Read(string answer)
    {
        int start = answer.IndexOf('{'), end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using JsonDocument json = JsonDocument.Parse(answer[start..(end + 1)]);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            string request = Text(json.RootElement, "request"), feature = Text(json.RootElement, "feature").ToLowerInvariant();
            if (request.Length is 0 or > 200 || !Features.Contains(feature)) return null;
            return new Rewrite(request, feature);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string Text(JsonElement json, string name) =>
        json.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? string.Join(' ', (value.GetString() ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            : "";

    static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"^(?:and|but|also|then|so|what\s+about|how\s+about|same\s+for|а|и|ами|ами\s+за|също|после)\b|\b(?:it|its|that|this|those|them|he|him|his|she|her|they|one|ones|first|second|third|last|other|same|again|той|тя|те|него|нея|тях|това|онова|този|онзи|първ\w*|втор\w*|трет\w*|последн\w*|пак|също)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Leaning();
}
