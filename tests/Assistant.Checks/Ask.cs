using System.Text;
using System.Text.Json;
using Assistant.Core;
using Assistant.Core.Actions;
using Assistant.Core.Knowledge;
using Assistant.Core.Llm;
using Assistant.Core.Storage;

namespace Assistant.Checks;

/// <summary>
/// "--ask": sends the everyday messages in cases\ask.txt through the assistant with the REAL cloud chat (the key in
/// Documents\Assistant\config.json) and the real Wikipedia, and writes what came back to ask-results.txt: which part
/// answered, the buttons offered, the action a rule chose, and the reply. The ordinary checks can't show how the cloud
/// model itself behaves; this does.
/// Still stand-ins: the local model, files, the screen, activity and the actions (nothing is done to this PC), and
/// the saved facts are the made-up ones in cases\memory. Only the made-up messages go out, logged in sent\ like any
/// other. Each line of ask.txt:   expected | the message | words the reply must contain (optional)
/// "expected" is a route, with a word from the button or action in [brackets] if one is expected; " or " between
/// several that are all fine. Free cloud keys allow only so much a minute, so it waits between questions.
/// </summary>
static class Ask
{
    public static async Task<int> RunAsync(string cases)
    {
        string file = Path.Combine(cases, "ask.txt");
        if (!File.Exists(file))
        {
            Console.WriteLine($"The questions file is missing: {file}");
            return 1;
        }

        DataFolder data = DataFolder.Default();
        if (!File.Exists(data.ConfigFile))
        {
            Console.WriteLine($"No settings found ({data.ConfigFile}). Run the app once and connect cloud chat first.");
            return 1;
        }
        AppConfig config = AppConfig.Load(data.ConfigFile);

        // The same providers, in the same order, as the app (App.xaml.cs).
        CloudEndpoint[] endpoints =
        [
            new("Groq", new Uri("https://api.groq.com/openai/v1/"), config.GroqApiKey, config.GroqModel, "models")
            {
                FallbackModels = ["openai/gpt-oss-120b", "llama-3.3-70b-versatile", "openai/gpt-oss-20b", "llama-3.1-8b-instant"],
            },
            new("OpenRouter", new Uri("https://openrouter.ai/api/v1/"), config.OpenRouterApiKey, config.OpenRouterModel, "key"),
        ];
        if (!endpoints.Any(endpoint => endpoint.HasKey))
        {
            Console.WriteLine("Cloud chat isn't connected (no Groq or OpenRouter key in Settings > Privacy and cloud), so there is nothing to ask.");
            return 1;
        }
        var cloudHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
        var wikiHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        wikiHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Assistant/0.8 (+https://github.com/ciberwarrior39-sys/Assistant)");
        var cloud = new CloudChat(endpoints.Where(endpoint => endpoint.HasKey).Select(endpoint => new OpenAiCompatibleProvider(endpoint, cloudHttp)).ToList(),
                                  new SentLog(data.SentFolder));
        var wikipedia = new WikipediaLookup(wikiHttp);

        string temp = Path.Combine(Path.GetTempPath(), "assistant-ask-" + Guid.NewGuid().ToString("N"));
        var report = new StringBuilder();
        report.AppendLine($"Assistant --ask, {DateTime.Now:yyyy-MM-dd HH:mm}");
        report.AppendLine();
        int number = 0, fine = 0, look = 0;
        try
        {
            foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                string[] fields = line.Split('|', 3, StringSplitOptions.TrimEntries);
                if (fields.Length < 2) continue;
                string expected = fields[0], message = fields[1], mustSay = fields.Length > 2 ? fields[2] : "";
                number++;

                var actions = new FakeActions();
                string route, text = "", buttons = "", detail = "", answeredBy = "";
                bool askedCloud = false;
                try
                {
                    AssistantService assistant = Program.Build("normal", Path.Combine(temp, number.ToString()), Path.Combine(cases, "memory"),
                                                               actions, cloud, wikipedia);
                    AssistantReply reply = await assistant.SendAsync(message, CancellationToken.None);
                    route = reply.Route;
                    text = reply.Text;
                    buttons = string.Join(", ", reply.Options.Select(option => option.Label));
                    detail = buttons + " " + string.Join(" ", reply.Options.Select(option => option.Intent?.ToString())) + " " + actions.Last;
                    askedCloud = reply.Source != "local";
                    if (askedCloud) answeredBy = LastModel(data.SentFolder);
                }
                catch (Exception ex)
                {
                    route = $"error: {ex.GetType().Name}: {ex.Message}";
                }

                bool routeFits = expected.Split(" or ", StringSplitOptions.TrimEntries).Any(alternative => Fits(alternative, route, detail));
                // Compared without spaces: models write "86 400" with whichever thin or non-breaking space they like.
                string squeezed = Squeeze(text);
                bool says = mustSay.Length == 0 || mustSay.Split('/', StringSplitOptions.TrimEntries).Any(word => squeezed.Contains(Squeeze(word), StringComparison.OrdinalIgnoreCase));
                bool ok = routeFits && says;
                if (ok) fine++;
                else look++;

                report.AppendLine($"#{number} {(ok ? "OK   " : "CHECK")} {route}{(answeredBy.Length > 0 ? $"  ({answeredBy})" : "")}");
                report.AppendLine($"  Q: {message}");
                report.AppendLine($"  expected: {expected}{(mustSay.Length > 0 ? $", saying {mustSay}" : "")}");
                if (buttons.Length > 0) report.AppendLine($"  buttons: {buttons}");
                if (actions.Last is { } ran) report.AppendLine($"  action: {ran}");
                report.AppendLine($"  A: {OneLine(text)}");
                report.AppendLine();
                Console.WriteLine($"#{number} {(ok ? "ok   " : "CHECK")} {route,-22} | {message}");

                // The free limit counts tokens a minute, and a lookup sends a few thousand.
                if (askedCloud) await Task.Delay(TimeSpan.FromSeconds(route.Contains("lookup", StringComparison.Ordinal) ? 20 : 9));
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leftover temporary files are harmless.
            }
        }

        report.Insert(0, $"{fine} as expected, {look} to look at, of {number}.{Environment.NewLine}");
        string results = Path.GetFullPath("ask-results.txt");
        File.WriteAllText(results, report.ToString(), Encoding.UTF8);
        Console.WriteLine();
        Console.WriteLine($"{fine} as expected, {look} to look at. Everything, with the replies, is in {results}");
        return 0;
    }

    // "cloud>offer [Volume up]": the route (also after "local>"), and the bracketed words somewhere in the buttons or action.
    static bool Fits(string alternative, string route, string detail)
    {
        string want = alternative, needle = "";
        int bracket = alternative.IndexOf('[');
        if (bracket >= 0)
        {
            want = alternative[..bracket].Trim();
            needle = alternative[(bracket + 1)..].TrimEnd(']', ' ').Trim();
        }
        bool routeFits = route == want || route.EndsWith(">" + want, StringComparison.Ordinal);
        return routeFits && (needle.Length == 0 || detail.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    // Which model wrote the last answer (the stand-in after a "too many requests" answers differently): from the sent\ log.
    static string LastModel(string sentFolder)
    {
        try
        {
            string today = Path.Combine(sentFolder, $"{DateTime.Now:yyyy-MM-dd}.jsonl");
            if (!File.Exists(today)) return "";
            string? last = File.ReadLines(today, Encoding.UTF8).LastOrDefault(line => line.Length > 0);
            if (last is null) return "";
            using JsonDocument entry = JsonDocument.Parse(last);
            string model = entry.RootElement.TryGetProperty("model", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "";
            string outcome = entry.RootElement.TryGetProperty("outcome", out JsonElement how) && how.ValueKind == JsonValueKind.String ? how.GetString() ?? "" : "";
            return outcome.StartsWith("failed", StringComparison.Ordinal) ? $"{model}, {outcome}" : model;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return "";
        }
    }

    static string Squeeze(string text) => new(text.Where(letter => !char.IsWhiteSpace(letter)).ToArray());

    static string OneLine(string text)
    {
        string flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > 500 ? flat[..500] + "…" : flat;
    }
}
