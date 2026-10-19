using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core.Llm;
using Assistant.Core.Search;

namespace Assistant.Core.Tools;

/// <summary>
/// search_files: the model fills in keywords, file types and a time period from a fixed list;
/// the code validates everything and does the date math (rules from the model test).
/// </summary>
public sealed class SearchFilesTool(IFileSearch search) : ITool
{
    const int MaxKeywords = 5;
    const int MaxExtensions = 10;
    const int MaxResults = 10;

    static readonly string[] Periods = ["any", "today", "past_week", "past_month", "past_year"];

    // Small models often write a kind of file instead of an extension, in either field.
    internal static readonly Dictionary<string, string[]> KindsOfFile = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spreadsheet"] = ["xlsx", "xls", "csv"],
        ["spreadsheets"] = ["xlsx", "xls", "csv"],
        ["excel"] = ["xlsx", "xls"],
        ["document"] = ["docx", "doc", "pdf", "txt"],
        ["documents"] = ["docx", "doc", "pdf", "txt"],
        ["word"] = ["docx", "doc"],
        ["photo"] = ["jpg", "jpeg", "png", "heic"],
        ["photos"] = ["jpg", "jpeg", "png", "heic"],
        ["image"] = ["jpg", "jpeg", "png", "gif", "webp"],
        ["images"] = ["jpg", "jpeg", "png", "gif", "webp"],
        ["picture"] = ["jpg", "jpeg", "png"],
        ["pictures"] = ["jpg", "jpeg", "png"],
        ["video"] = ["mp4", "mov", "mkv", "avi"],
        ["videos"] = ["mp4", "mov", "mkv", "avi"],
        ["music"] = ["mp3", "wav", "flac", "m4a"],
        ["presentation"] = ["pptx", "ppt"],
        ["presentations"] = ["pptx", "ppt"],
        ["powerpoint"] = ["pptx", "ppt"],
        ["slides"] = ["pptx", "ppt"],
        ["slideshow"] = ["pptx", "ppt"],
        ["deck"] = ["pptx", "ppt"],
        ["sheet"] = ["xlsx", "xls", "csv"],
        ["sheets"] = ["xlsx", "xls", "csv"],
        ["doc"] = ["docx", "doc"],
        ["docs"] = ["docx", "doc", "pdf"],
        ["pic"] = ["jpg", "jpeg", "png"],
        ["pics"] = ["jpg", "jpeg", "png"],
        // Bulgarian
        ["презентация"] = ["pptx", "ppt"],
        ["презентацията"] = ["pptx", "ppt"],
        ["презентации"] = ["pptx", "ppt"],
        ["презентациите"] = ["pptx", "ppt"],
        ["документ"] = ["docx", "doc", "pdf", "txt"],
        ["документа"] = ["docx", "doc", "pdf", "txt"],
        ["документът"] = ["docx", "doc", "pdf", "txt"],
        ["документи"] = ["docx", "doc", "pdf", "txt"],
        ["документите"] = ["docx", "doc", "pdf", "txt"],
        ["снимка"] = ["jpg", "jpeg", "png", "heic"],
        ["снимката"] = ["jpg", "jpeg", "png", "heic"],
        ["снимки"] = ["jpg", "jpeg", "png", "heic"],
        ["снимките"] = ["jpg", "jpeg", "png", "heic"],
        ["картинка"] = ["jpg", "jpeg", "png", "gif", "webp"],
        ["картинки"] = ["jpg", "jpeg", "png", "gif", "webp"],
        ["таблица"] = ["xlsx", "xls", "csv"],
        ["таблицата"] = ["xlsx", "xls", "csv"],
        ["таблици"] = ["xlsx", "xls", "csv"],
        ["видео"] = ["mp4", "mov", "mkv", "avi"],
        ["видеото"] = ["mp4", "mov", "mkv", "avi"],
        ["видеа"] = ["mp4", "mov", "mkv", "avi"],
        ["музика"] = ["mp3", "wav", "flac", "m4a"],
        ["песен"] = ["mp3", "wav", "flac", "m4a"],
        ["песента"] = ["mp3", "wav", "flac", "m4a"],
        ["песни"] = ["mp3", "wav", "flac", "m4a"],
        ["пдф"] = ["pdf"],
        ["pdf"] = ["pdf"],
        ["docx"] = ["docx"],
        ["xlsx"] = ["xlsx"],
        ["pptx"] = ["pptx"],
        ["txt"] = ["txt"],
        ["jpg"] = ["jpg", "jpeg"],
        ["png"] = ["png"],
        ["mp3"] = ["mp3"],
        ["mp4"] = ["mp4"],
    };

    // Words from the request itself, not from a file name ("documents I edited today").
    internal static readonly HashSet<string> RequestWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "files", "my", "the", "a", "an", "i", "find", "search", "open", "show", "where",
        "edit", "edited", "modified", "changed", "created", "made", "saved", "worked",
        "recent", "recently", "latest", "last", "today", "yesterday", "week", "month", "year",
        "къде", "е", "са", "ми", "моя", "моят", "моите", "моето", "мой", "моя", "намери", "покажи", "отвори", "файл", "файла",
        "файлът", "файлове", "файловете", "за", "от", "на", "в", "във", "с", "със", "и", "днес", "вчера", "последния", "последната",
    };

    public ToolDefinition Definition { get; } = new(
        "search_files",
        "Find files on the user's computer by words in the file name, file type, and how recently they were modified.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["keywords"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Words from the file NAME only, e.g. [\"budget\"]. Leave empty if the user did not say a name.",
                },
                ["extensions"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "File types without dots, e.g. [\"xlsx\", \"csv\"]. Leave empty for any type.",
                },
                ["modified"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("any", "today", "past_week", "past_month", "past_year"),
                    ["description"] = "How recently the file was modified. Use any unless the user said when.",
                },
            },
        });

    public async Task<ToolOutcome> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        var nameWords = new List<string>();
        var types = new List<string>();

        foreach (string raw in ReadStrings(arguments, "extensions"))
        {
            types.AddRange(KindsOfFile.TryGetValue(raw.Trim().TrimStart('.'), out string[]? mapped)
                ? mapped
                : new[] { CleanExtension(raw) });
        }

        // Sort the model's keywords: kinds of file become extensions, request words are dropped,
        // and only real name words are searched for.
        // Each word is searched on its own, so "tax return" finds "Tax_Return_2025.pdf" and word order doesn't matter.
        foreach (string raw in ReadStrings(arguments, "keywords"))
        {
            foreach (string word in CleanKeyword(raw).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (KindOf(word) is { } kind) types.AddRange(kind);
                else if (!RequestWords.Contains(word)) nameWords.Add(word);
            }
        }

        string[] keywords = nameWords.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxKeywords).ToArray();
        string[] extensions = types.Where(type => type.Length > 0).Distinct().Take(MaxExtensions).ToArray();
        string period = ReadString(arguments, "modified") is { } value && Periods.Contains(value) ? value : "any";

        // A time period alone is a valid search ("files I edited today"); nothing at all is not.
        if (keywords.Length == 0 && extensions.Length == 0 && period == "any")
            return new ToolOutcome("No search words were given. Ask the user what the file is called.")
            {
                Reply = "What's the file called? A word or two from its name is enough.",
            };

        DateTime? after = ModifiedAfter(period, DateTime.Now);
        FileSearchResult result = await search.SearchAsync(new FileSearchQuery(keywords, extensions, after, MaxResults), ct);

        if (!result.Available)
            return new ToolOutcome($"File search is unavailable: {result.Problem}. Tell the user.")
            {
                Reply = $"File search isn't working right now: {result.Problem}",
            };

        // Nothing with every word: try looser searches, most specific first, and say which one found something.
        string? looser = null;
        if (result.Files.Count == 0)
        {
            foreach ((string[] words, string[] types2, string note) in Relaxed(keywords, extensions))
            {
                FileSearchResult retry = await search.SearchAsync(new FileSearchQuery(words, types2, after, MaxResults), ct);
                if (retry.Files.Count == 0) continue;
                result = retry;
                looser = note;
                break;
            }
        }

        if (result.Files.Count == 0)
            return new ToolOutcome("No matching files were found.")
            {
                NothingFound = true,
                Reply = keywords.Length > 0
                    ? $"I couldn't find a file with \"{string.Join(" ", keywords)}\" in its name. Try another word from the name."
                    : "I couldn't find any files like that.",
            };

        // The model gets names and dates only; the app shows the full paths itself.
        var text = new StringBuilder($"Found {result.Files.Count} file(s), newest first:\n");
        foreach (FileHit file in result.Files) text.AppendLine($"- {file.FileName} (modified {file.Modified:yyyy-MM-dd})");
        string found = result.Files.Count == 1 ? "here's the file I found." : $"I found {result.Files.Count} files, newest first.";
        return new ToolOutcome(text.ToString())
        {
            Files = result.Files,
            Reply = looser is null ? char.ToUpperInvariant(found[0]) + found[1..] : $"{looser}, {found}",
        };
    }

    /// <summary>
    /// Looser searches for when nothing matched every word, in order: the same words in any file type; the words
    /// with Latin and Cyrillic letters swapped ("splavi" and "сплави"); then each word alone, longest first.
    /// </summary>
    static IEnumerable<(string[] Words, string[] Types, string Note)> Relaxed(string[] keywords, string[] extensions)
    {
        string all = string.Join(" ", keywords);
        string[] anyType = Array.Empty<string>();
        if (keywords.Length > 0 && extensions.Length > 0)
            yield return (keywords, anyType, $"Nothing of that type has \"{all}\" in its name. In other types");

        string[] swapped = keywords.Select(word => Transliteration.Swap(word) ?? word).ToArray();
        if (!swapped.SequenceEqual(keywords))
        {
            string spelled = $"Nothing has \"{all}\" in its name. Spelled \"{string.Join(" ", swapped)}\"";
            yield return (swapped, extensions, spelled);
            if (extensions.Length > 0) yield return (swapped, anyType, spelled);
        }

        if (keywords.Length > 1)
        {
            foreach (string word in keywords.Where(word => word.Length >= 3).OrderByDescending(word => word.Length))
            {
                string partly = $"Nothing has all of \"{all}\" in its name. With just \"{word}\"";
                yield return (new[] { word }, extensions, partly);
                if (extensions.Length > 0) yield return (new[] { word }, anyType, partly);
            }
        }
    }

    /// <summary>A kind of file ("spreadsheet", "презентация"), also when slightly misspelled ("prezentation").</summary>
    internal static string[]? KindOf(string word)
    {
        if (KindsOfFile.TryGetValue(word, out string[]? kind)) return kind;
        if (word.Length < 6) return null; // short words are too easy to confuse with names
        int allowed = word.Length >= 9 ? 2 : 1;
        return KindsOfFile
            .Where(entry => entry.Key.Length >= 6 && Math.Abs(entry.Key.Length - word.Length) <= allowed)
            .Select(entry => (entry.Value, Distance: EditDistance(entry.Key, word.ToLowerInvariant())))
            .Where(match => match.Distance <= allowed)
            .OrderBy(match => match.Distance)
            .Select(match => match.Value)
            .FirstOrDefault();
    }

    static int EditDistance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    static DateTime? ModifiedAfter(string period, DateTime now) => period switch
    {
        "today" => now.Date,
        "past_week" => now.AddDays(-7),
        "past_month" => now.AddMonths(-1),
        "past_year" => now.AddYears(-1),
        _ => null,
    };

    // Keeps letters, digits and a few safe characters, so the model cannot inject Everything search syntax.
    static string CleanKeyword(string raw)
    {
        string kept = new(raw.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.').ToArray());
        string collapsed = string.Join(' ', kept.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > 50 ? collapsed[..50] : collapsed;
    }

    static string CleanExtension(string raw)
    {
        string kept = new(raw.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return kept.Length is > 0 and <= 10 ? kept : "";
    }

    // Accepts a JSON array of strings or a single comma-separated string.
    static IEnumerable<string> ReadStrings(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out JsonElement value))
            return [];

        var strings = new List<string>();
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) strings.AddRange(Split(item.GetString()));
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            strings.AddRange(Split(value.GetString()));
        }
        return strings;
    }

    static string[] Split(string? text) =>
        (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static string? ReadString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim().ToLowerInvariant()
            : null;
}
