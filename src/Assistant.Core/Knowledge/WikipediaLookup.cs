using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Core.Diagnostics;
using Assistant.Core.Llm;
using Assistant.Core.Memory;

namespace Assistant.Core.Knowledge;

/// <summary>What Wikipedia says about a question: the relevant parts of an article or two, and the articles' links.</summary>
public sealed record LookedUp(string Text, IReadOnlyList<WebSource> Sources);

/// <summary>
/// Looks a factual question up on Wikipedia (Bulgarian for a question in Cyrillic, English otherwise) and returns the
/// parts of the top articles that share words with it, about 7,000 characters at most. The cloud model then answers
/// from that text, which costs a few thousand tokens instead of the tens of thousands a web search inside the model
/// takes, and it can't skip reading it. Only the search words go to Wikipedia. Null when nothing useful was found.
/// </summary>
public sealed partial class WikipediaLookup(HttpClient http)
{
    const int MaxArticles = 2;
    const int MaxCharsPerArticle = 3500;
    const int IntroParagraphs = 2;

    static readonly HashSet<string> NotSearchWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "who", "whom", "whose", "what", "when", "where", "which", "why", "how", "is", "are", "was", "were", "do", "does", "did",
        "can", "could", "should", "would", "will", "the", "a", "an", "of", "in", "on", "at", "to", "for", "and", "or", "about",
        "tell", "me", "explain", "describe", "please", "you", "know", "it", "its", "this", "that", "there", "many", "much",
        "кой", "коя", "кое", "кои", "какво", "какъв", "каква", "какви", "кога", "къде", "защо", "как", "колко", "чий", "чия",
        "е", "са", "бил", "била", "било", "били", "на", "в", "във", "за", "от", "с", "със", "и", "или", "да", "се", "ли", "ми",
        "разкажи", "обясни", "опиши", "кажи", "знаеш", "моля", "това", "тази", "този", "тези", "има", "по", "до",
        // Wikipedia's search wants every word to be in the article, and these are usually there in another form
        // ("wrote" in the question, "written by" in the article), which hid the right article.
        "wrote", "written", "write", "writes", "made", "built", "won", "sang", "sung", "drew", "drawn", "began", "led", "fought",
        "написал", "написала", "написали", "написа", "нарисувал", "нарисувала", "създал", "създала", "открил", "открила",
        "изобретил", "основал", "построил", "изпял", "изпяла", "режисирал", "композирал",
    };

    /// <param name="question">A question that already passed the privacy check for cloud chat.</param>
    public async Task<LookedUp?> FindAsync(string question, CancellationToken ct)
    {
        string language = question.Any(c => c is >= 'А' and <= 'я') ? "bg" : "en";
        string query = SearchWords(question);
        if (query.Length < 3) return null;

        try
        {
            IReadOnlyList<string> titles = await SearchAsync(language, query, ct);
            // A name in the question ("Under the Yoke", "Христо Ботев") is searched by itself too, and its best match
            // is read first: the article with that title, whatever else the question asks about it.
            if (!Quoted().IsMatch(question) && Name(question) is { } name && !string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
            {
                IReadOnlyList<string> named = await SearchAsync(language, name, ct);
                titles = named.Take(1).Concat(titles).Distinct().ToList();
            }
            var text = new StringBuilder();
            var sources = new List<WebSource>();
            foreach (string title in titles.Take(MaxArticles))
            {
                string extract = await ExtractAsync(language, title, ct);
                if (!IsAbout(question, title, extract)) continue;
                string relevant = Relevant(question, extract);
                if (relevant.Length == 0) continue;
                text.Append($"Wikipedia, «{title}»:\n{relevant}\n\n");
                sources.Add(new WebSource($"{title} (Wikipedia)", $"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}"));
            }
            return sources.Count == 0 ? null : new LookedUp(text.ToString().Trim(), sources);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
                                       or KeyNotFoundException)
        {
            DiagnosticLog.Error("Wikipedia lookup failed", ex);
            return null;
        }
    }

    // A quoted title is the best search ("Бай Ганьо журналист"); otherwise the question without its question words.
    static string SearchWords(string question)
    {
        if (Quoted().Match(question) is { Success: true } quoted && quoted.Groups["title"].Value.Trim().Length >= 3)
            return quoted.Groups["title"].Value.Trim();
        string[] words = Word().Matches(question).Select(match => match.Value).Where(word => !NotSearchWords.Contains(word)).ToArray();
        return string.Join(' ', words.Take(8));
    }

    /// <summary>
    /// The longest run of capitalised words in a question, small joining words allowed inside ("Under the Yoke",
    /// "Mona Lisa", "Христо Ботев"); null when there is none. The first word doesn't count: every sentence starts
    /// with a capital.
    /// </summary>
    public static string? Name(string question)
    {
        string text = question.Trim();
        Match first = Word().Match(text);
        if (!first.Success) return null;
        string rest = text[(first.Index + first.Length)..];
        string? best = null;
        foreach (Match match in NameRun().Matches(rest))
        {
            if (match.Value.Length >= 4 && (best is null || match.Value.Length > best.Length)) best = match.Value;
        }
        return best;
    }

    async Task<IReadOnlyList<string>> SearchAsync(string language, string query, CancellationToken ct)
    {
        string url = $"https://{language}.wikipedia.org/w/api.php?action=query&list=search&srlimit={MaxArticles}&format=json" +
                     $"&formatversion=2&srsearch={Uri.EscapeDataString(query)}";
        using JsonDocument document = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        return document.RootElement.GetProperty("query").GetProperty("search").EnumerateArray()
            .Select(result => result.GetProperty("title").GetString() ?? "")
            .Where(title => title.Length > 0)
            .ToList();
    }

    async Task<string> ExtractAsync(string language, string title, CancellationToken ct)
    {
        string url = $"https://{language}.wikipedia.org/w/api.php?action=query&prop=extracts&explaintext=1&exsectionformat=plain" +
                     $"&redirects=1&format=json&formatversion=2&titles={Uri.EscapeDataString(title)}";
        using JsonDocument document = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        JsonElement pages = document.RootElement.GetProperty("query").GetProperty("pages");
        return pages.GetArrayLength() > 0 && pages[0].TryGetProperty("extract", out JsonElement extract) ? extract.GetString() ?? "" : "";
    }

    // An article is used only when it is about the question: its title shares a key word with it, or at least half of
    // the question's key words appear in the text. Otherwise the search just returned its nearest guess.
    static bool IsAbout(string question, string title, string extract)
    {
        string[] keys = MemoryMatch.KeyWords(question);
        if (keys.Length == 0) return false;
        string[] titleWords = Word().Matches(title.ToLowerInvariant()).Select(match => match.Value).ToArray();
        if (keys.Any(key => titleWords.Any(word => RelevantFacts.Similar(key, word)))) return true;

        var textWords = Word().Matches(extract.ToLowerInvariant()).Select(match => match.Value).ToHashSet();
        int found = keys.Count(key => textWords.Contains(key) || textWords.Any(word => RelevantFacts.Similar(key, word)));
        return found * 2 >= keys.Length;
    }

    // The article's opening, then the paragraphs that share the most words with the question, in the article's order.
    static string Relevant(string question, string extract)
    {
        var paragraphs = extract.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(paragraph => paragraph.Length > 40)
            .ToList();
        if (paragraphs.Count == 0) return "";

        var chosen = new HashSet<string>(paragraphs.Take(IntroParagraphs));
        int room = MaxCharsPerArticle - chosen.Sum(paragraph => paragraph.Length);
        foreach (string paragraph in RelevantFacts.Pick(question, paragraphs.Skip(IntroParagraphs), max: 8, maxChars: Math.Max(0, room)))
            chosen.Add(paragraph);

        string text = string.Join("\n", paragraphs.Where(chosen.Contains));
        return text.Length > MaxCharsPerArticle ? text[..MaxCharsPerArticle] + "…" : text;
    }

    [GeneratedRegex(@"[""„“«](?<title>[^""„“”«»]{3,80})[""“”»]")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"\p{Lu}[\p{L}\p{N}'’-]*(?:\s+(?:(?:of|the|and|in|de|la|von|van|на|и|в|за|от)\s+){0,2}\p{Lu}[\p{L}\p{N}'’-]*)*")]
    private static partial Regex NameRun();
}
