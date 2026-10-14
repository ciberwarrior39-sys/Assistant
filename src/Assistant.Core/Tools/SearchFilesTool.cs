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
    static readonly Dictionary<string, string[]> KindsOfFile = new(StringComparer.OrdinalIgnoreCase)
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
    static readonly HashSet<string> RequestWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "files", "my", "the", "a", "an", "i", "find", "search", "open", "show", "where",
        "edit", "edited", "modified", "changed", "created", "made", "saved", "worked",
        "recent", "recently", "latest", "last", "today", "yesterday", "week", "month", "year",
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
        foreach (string raw in ReadStrings(arguments, "keywords"))
        {
            var kept = new List<string>();
            foreach (string word in CleanKeyword(raw).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (KindsOfFile.TryGetValue(word, out string[]? kind)) types.AddRange(kind);
                else if (!RequestWords.Contains(word)) kept.Add(word);
            }
            if (kept.Count > 0) nameWords.Add(string.Join(' ', kept));
        }

        string[] keywords = nameWords.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxKeywords).ToArray();
        string[] extensions = types.Where(type => type.Length > 0).Distinct().Take(MaxExtensions).ToArray();
        string period = ReadString(arguments, "modified") is { } value && Periods.Contains(value) ? value : "any";

        // A time period alone is a valid search ("files I edited today"); nothing at all is not.
        if (keywords.Length == 0 && extensions.Length == 0 && period == "any")
            return new ToolOutcome("No search words were given. Ask the user what the file is called.");

        var query = new FileSearchQuery(keywords, extensions, ModifiedAfter(period, DateTime.Now), MaxResults);
        FileSearchResult result = await search.SearchAsync(query, ct);

        if (!result.Available)
            return new ToolOutcome($"File search is unavailable: {result.Problem}. Tell the user.");
        if (result.Files.Count == 0)
            return new ToolOutcome("No matching files were found.");

        // The model gets names and dates only; the app shows the full paths itself.
        var text = new StringBuilder($"Found {result.Files.Count} file(s), newest first:\n");
        foreach (FileHit file in result.Files) text.AppendLine($"- {file.FileName} (modified {file.Modified:yyyy-MM-dd})");
        return new ToolOutcome(text.ToString()) { Files = result.Files };
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
