using System.Text.RegularExpressions;

namespace Assistant.Core;

/// <summary>
/// A typed request about the marked text ("translate this", "summarise the selected text", "преведи това"): the app
/// picks up the marked text like "Help with selected text" does, then runs the task. Task null: just show the card.
/// Translate: which way is decided by the text (Cyrillic → English, anything else → Bulgarian) unless the user said.
/// </summary>
/// Pasted: the text came with the request ("is this a scam? <the message>"), so there's nothing to pick up.
public sealed record TextHelpRequest(TextTask? Task, bool TranslateEitherWay = false, string? Pasted = null);

public static partial class TextHelpRequests
{
    /// <summary>Null when the message isn't about the marked text (then it's ordinary chat).</summary>
    public static TextHelpRequest? FromMessage(string message)
    {
        if (ScamRequest(message) is { } scam) return scam;
        string text = message.Trim().ToLowerInvariant().TrimEnd('.', '!', '?', ' ');
        if (text.Length == 0 || text.Length > 120) return null;

        if (Translate().Match(text) is { Success: true } translate)
        {
            string to = translate.Groups["to"].Value;
            return to.Length == 0 ? new TextHelpRequest(null, TranslateEitherWay: true)
                : to.StartsWith("eng", StringComparison.Ordinal) || to.StartsWith("анг", StringComparison.Ordinal) ? new TextHelpRequest(TextTask.ToEnglish)
                : new TextHelpRequest(TextTask.ToBulgarian);
        }
        if (Summarise().IsMatch(text)) return new TextHelpRequest(TextTask.Summarise);
        if (Grammar().IsMatch(text)) return new TextHelpRequest(TextTask.FixGrammar);
        if (Shorter().IsMatch(text)) return new TextHelpRequest(TextTask.Shorter);
        if (Explain().IsMatch(text)) return new TextHelpRequest(TextTask.Explain);
        if (Marked().IsMatch(text)) return new TextHelpRequest(null); // "help with the selected text"
        return null;
    }

    /// <summary>
    /// "is this a scam?" about the marked text, or with the message pasted before or after it ("is this a scam: …",
    /// "… \n\nis this legit?"). A bare "is this safe?" stays chat unless something is pasted with it.
    /// </summary>
    static TextHelpRequest? ScamRequest(string message)
    {
        string trimmed = message.Trim();
        // The question first, the message after it (same line after ":" or on the next lines).
        Match start = ScamQuestionStart().Match(trimmed);
        if (start.Success)
        {
            string rest = trimmed[start.Length..].Trim();
            if (rest.Length == 0) return start.Groups["strong"].Success ? new TextHelpRequest(TextTask.ScamCheck) : null;
            // "is it real that …" is a question, not a pasted message: without a scam word, the message must be set apart
            // (after "?", ":" or a new line) or be a link.
            string separator = start.Groups["sep"].Value;
            bool setApart = start.Groups["strong"].Success || separator.IndexOfAny(['?', ':', '\n']) >= 0
                            || rest.Contains("http", StringComparison.OrdinalIgnoreCase) || rest.Contains("www.", StringComparison.OrdinalIgnoreCase);
            if (setApart && Pasted(rest)) return new TextHelpRequest(TextTask.ScamCheck, Pasted: rest);
        }

        // The message first, the question on the last line.
        string[] lines = trimmed.Split('\n');
        if (lines.Length >= 2)
        {
            string last = lines[^1].Trim();
            Match end = ScamQuestionStart().Match(last);
            if (end.Success && end.Length >= last.TrimEnd('?', '!', '.', ' ').Length)
            {
                string before = string.Join("\n", lines[..^1]).Trim();
                if (Pasted(before)) return new TextHelpRequest(TextTask.ScamCheck, Pasted: before);
            }
        }
        return null;
    }

    // Enough to judge: a link, or a sentence or two.
    static bool Pasted(string text) => text.Length >= 25 || text.Contains("http", StringComparison.OrdinalIgnoreCase) || text.Contains("www.", StringComparison.OrdinalIgnoreCase);

    // "strong" phrases name a scam outright, so they mean the marked text even with nothing pasted; "is this safe/real/legit"
    // could be about anything, so those need a pasted message.
    [GeneratedRegex("""
        ^(?:please\s+|can\s+you\s+|could\s+you\s+|моля\s+|можеш\s+ли\s+да\s+)?
        (?:
          (?<strong>
            (?:is|was)\s+(?:this|that|it)\s+(?:a\s+)?(?:scam|phishing|fraud|spam)(?:\s+(?:message|email|e-mail|text|link|sms))?
          | (?:check|tell\s+me)\s+(?:if\s+)?(?:this|that|it)\s+(?:is\s+)?(?:a\s+)?(?:scam|phishing|fraud)
          | (?:scam|phishing)\s+check
          | check\s+(?:this|that)\s+(?:message|email|e-mail|text|sms|link)(?:\s+for\s+(?:scams?|phishing|fraud))?
          | (?:това|то)\s+(?:измама|фишинг|спам)\s+ли\s+е | измама\s+ли\s+е(?:\s+(?:това|то))? | фишинг\s+ли\s+е(?:\s+това)?
          | (?:провери|виж)\s+(?:дали\s+)?(?:това|този|тази)\s+(?:е\s+)?(?:измама|съобщение|имейл|линк|смс)(?:\s+(?:за\s+)?измама)?
          )
        | (?:is|was)\s+(?:this|that|it)\s+(?:legit|legitimate|real|safe|trustworthy|genuine|fake)(?:\s+(?:message|email|e-mail|text|link|sms|site|website))?
        | is\s+this\s+(?:link|site|website|message|email|text)\s+(?:safe|legit|real|fake)
        | (?:това|този\s+линк|този\s+сайт|това\s+съобщение)\s+(?:истинско|истински|безопасно|безопасен|сигурно|сигурен|фалшиво|фалшив)\s+ли\s+е
        | (?:истинско|безопасно|сигурно|фалшиво)\s+ли\s+е(?:\s+това)?
        )
        (?<sep>[\s?!.:,\-–—]*)
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex ScamQuestionStart();

    // What the request points at: "this", "it", "the selected/marked/highlighted text"; "това", "го", "маркирания текст".
    const string This = @"(this|that|it|the\s(selected|marked|highlighted|copied)(\stext)?|(selected|marked|highlighted)\stext|това|го|маркиран(ия|ото)?(\sтекст)?|избран(ия|ото)?(\sтекст)?|селектиран(ия|ото)?(\sтекст)?)";

    [GeneratedRegex(@"^(please\s|can\syou\s|could\syou\s|моля\s|можеш\sли\sда\s)?(translate|преведи)\s" + This + @"(\s(to|into|на)\s(?<to>english|bulgarian|английски|български))?$")]
    private static partial Regex Translate();

    [GeneratedRegex(@"^(please\s|can\syou\s|моля\s)?(summari[sz]e|sum\sup|обобщи|резюмирай)\s" + This + "$")]
    private static partial Regex Summarise();

    [GeneratedRegex(@"^(please\s|can\syou\s|моля\s)?(fix|correct|check|поправи|провери|коригирай)\s(the\s)?(grammar|spelling|граматиката|правописа)?\s?(in\s|of\s|на\s)?" + This + "$|^(fix|поправи)\\s(the\\s)?(grammar|граматиката)$")]
    private static partial Regex Grammar();

    [GeneratedRegex(@"^(please\s|can\syou\s|моля\s)?(make\s" + This + @"\sshorter|shorten\s" + This + @"|съкрати\s" + This + ")$")]
    private static partial Regex Shorter();

    [GeneratedRegex(@"^(please\s|can\syou\s|моля\s)?(explain|обясни)\s" + This + @"$|^what\sdoes\sthe\s(selected|marked|highlighted)\stext\smean$|^какво\sозначава\s(маркираното|маркирания\sтекст|избраното)$")]
    private static partial Regex Explain();

    [GeneratedRegex(@"\b(selected|marked|highlighted)\stext\b|маркиран(ия|ото)\sтекст|маркираното")]
    private static partial Regex Marked();
}
