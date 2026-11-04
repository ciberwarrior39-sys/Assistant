using System.Text.RegularExpressions;

namespace Assistant.Core;

/// <summary>
/// A typed request about the marked text ("translate this", "summarise the selected text", "преведи това"): the app
/// picks up the marked text like "Help with selected text" does, then runs the task. Task null: just show the card.
/// Translate: which way is decided by the text (Cyrillic → English, anything else → Bulgarian) unless the user said.
/// </summary>
public sealed record TextHelpRequest(TextTask? Task, bool TranslateEitherWay = false);

public static partial class TextHelpRequests
{
    /// <summary>Null when the message isn't about the marked text (then it's ordinary chat).</summary>
    public static TextHelpRequest? FromMessage(string message)
    {
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
