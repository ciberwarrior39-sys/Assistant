using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core;
using Assistant.Core.Actions;
using Assistant.Core.Backup;
using Assistant.Core.Diagnostics;
using Assistant.Core.Knowledge;
using Assistant.Core.Llm;
using Assistant.Core.Memory;
using Assistant.Core.Organizer;
using Assistant.Core.Storage;
using Assistant.Core.Tools;

namespace Assistant.Checks;

/// <summary>
/// Sends every message in cases\routing.txt through the real AssistantService and compares which part of it answered
/// (AssistantReply.Route) with what the line expects. The local model, cloud chat, Wikipedia, the tools that touch
/// Windows and the app's actions are stand-ins, so the checks run in a second, the same on any computer, and nothing
/// leaves it. Each check gets a fresh assistant and a fresh copy of the made-up saved facts in cases\memory.
/// Exit code 0 when everything passed, 1 otherwise.   --all also lists the checks that passed.
/// </summary>
static class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        DiagnosticLog.Quiet = true; // the app's own log stays as it is
        bool showAll = args.Contains("--all");

        string cases = Path.Combine(AppContext.BaseDirectory, "cases");
        string file = Path.Combine(cases, "routing.txt");
        if (!File.Exists(file))
        {
            Console.WriteLine($"The checks file is missing: {file}");
            return 1;
        }

        string temp = Path.Combine(Path.GetTempPath(), "assistant-checks-" + Guid.NewGuid().ToString("N"));
        int passed = 0, failed = 0, number = 0;
        string setup = "normal";
        try
        {
            foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith('@'))
                {
                    setup = line[1..].Trim().ToLowerInvariant();
                    continue;
                }

                int bar = line.IndexOf('|');
                if (bar <= 0)
                {
                    Console.WriteLine($"SKIP  not \"route | message\": {line}");
                    continue;
                }
                string expected = line[..bar].Trim();
                string message = line[(bar + 1)..].Trim();

                string got;
                try
                {
                    AssistantService assistant = Build(setup, Path.Combine(temp, (++number).ToString()), Path.Combine(cases, "memory"));
                    got = (await assistant.SendAsync(message, CancellationToken.None)).Route;
                }
                catch (Exception ex)
                {
                    got = $"error: {ex.GetType().Name}: {ex.Message}";
                }

                if (got == expected)
                {
                    passed++;
                    if (showAll) Console.WriteLine($"ok    {expected,-22} | {message}");
                }
                else
                {
                    failed++;
                    Console.WriteLine($"FAIL  expected {expected}, got {got}  [{setup}] | {message}");
                }
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

        (int scamPassed, int scamFailed) = ScamChecks(Path.Combine(cases, "scam.txt"), showAll);
        passed += scamPassed;
        failed += scamFailed;

        (int backupPassed, int backupFailed) = BackupChecks(Path.Combine(temp, "backup"), showAll);
        passed += backupPassed;
        failed += backupFailed;

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? $"All {passed} checks passed." : $"{passed} of {passed + failed} checks passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// cases\scam.txt: the scam check's warning signs ("signs 0", "signs >=2") and which typed messages start it
    /// ("asks": about the marked text, "pasted": with the message in it, "chat": not a scam check). "\n" in a line is a new line.
    /// </summary>
    static (int Passed, int Failed) ScamChecks(string file, bool showAll)
    {
        if (!File.Exists(file)) return (0, 0);
        int passed = 0, failed = 0;
        foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int bar = line.IndexOf('|');
            if (bar <= 0) continue;
            string expected = line[..bar].Trim();
            string text = line[(bar + 1)..].Trim().Replace("\\n", "\n");

            string got;
            bool ok;
            if (expected.StartsWith("signs", StringComparison.Ordinal))
            {
                ScamReport report = ScamSigns.Check(text);
                got = $"signs {report.Signs.Count}" + (report.Signs.Count > 0 ? ": " + string.Join(" / ", report.Signs) : "");
                string want = expected["signs".Length..].Trim();
                ok = want.StartsWith(">=", StringComparison.Ordinal)
                    ? report.Signs.Count >= int.Parse(want[2..])
                    : report.Signs.Count == int.Parse(want);
            }
            else
            {
                TextHelpRequest? request = TextHelpRequests.FromMessage(text);
                got = request is not { Task: TextTask.ScamCheck } ? "chat" : request.Pasted is null ? "asks" : "pasted";
                ok = got == expected;
            }

            if (ok)
            {
                passed++;
                if (showAll) Console.WriteLine($"ok    {expected,-22} | {text.Replace("\n", " / ")}");
            }
            else
            {
                failed++;
                Console.WriteLine($"FAIL  expected {expected}, got {got}  [scam] | {text.Replace("\n", " / ")}");
            }
        }
        return (passed, failed);
    }

    /// <summary>
    /// Backups on real folders in the temp folder: a first copy, then a changed, a deleted and a new file (the old copies
    /// must land in Older versions), a run with nothing to do, a folder that went missing (its copy must stay), and old
    /// versions past 30 days being removed. Skipped files (Office lock files, node_modules) must never be copied.
    /// </summary>
    static (int Passed, int Failed) BackupChecks(string root, bool showAll)
    {
        int passed = 0, failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok)
            {
                passed++;
                if (showAll) Console.WriteLine($"ok    backup: {name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"FAIL  backup: {name} {detail}");
            }
        }

        try
        {
            string documents = Path.Combine(root, "pc", "Documents");
            string destination = Path.Combine(root, "usb");
            Directory.CreateDirectory(Path.Combine(documents, "sub"));
            Directory.CreateDirectory(Path.Combine(documents, "node_modules"));
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(documents, "a.txt"), "one");
            File.WriteAllText(Path.Combine(documents, "sub", "b.txt"), "two");
            File.WriteAllText(Path.Combine(documents, "~$open.docx"), "lock");
            File.WriteAllText(Path.Combine(documents, "node_modules", "x.js"), "tool");
            string[] folders = [documents];
            string current = Path.Combine(BackupEngine.PcFolder(destination), "Current", "Documents");
            string olderRoot = Path.Combine(BackupEngine.PcFolder(destination), "Older versions");
            var start = new DateTime(2026, 11, 1, 12, 0, 0);

            BackupOutcome first = BackupEngine.Run(folders, destination, "id", start, CancellationToken.None);
            Check("first backup copies the files", first.Finished && first.Added == 2 && File.ReadAllText(Path.Combine(current, "a.txt")) == "one",
                  first.Summary());
            Check("lock files and tool folders are left out",
                  !File.Exists(Path.Combine(current, "~$open.docx")) && !Directory.Exists(Path.Combine(current, "node_modules")));
            Check("a READ ME explains the backup", File.Exists(Path.Combine(destination, BackupEngine.FolderName, "READ ME.txt")));

            File.WriteAllText(Path.Combine(documents, "a.txt"), "one, changed");
            File.SetLastWriteTimeUtc(Path.Combine(documents, "a.txt"), DateTime.UtcNow.AddMinutes(5));
            File.Delete(Path.Combine(documents, "sub", "b.txt"));
            File.WriteAllText(Path.Combine(documents, "c.txt"), "three");
            BackupOutcome second = BackupEngine.Run(folders, destination, "id", start.AddMinutes(1), CancellationToken.None);
            string older = Path.Combine(olderRoot, start.AddMinutes(1).ToString("yyyy-MM-dd HH-mm-ss"), "Documents");
            Check("a changed file is copied again", second.Updated == 1 && File.ReadAllText(Path.Combine(current, "a.txt")) == "one, changed", second.Summary());
            Check("its old copy goes to Older versions", File.Exists(Path.Combine(older, "a.txt")) && File.ReadAllText(Path.Combine(older, "a.txt")) == "one");
            Check("a new file is added", second.Added == 1 && File.Exists(Path.Combine(current, "c.txt")), second.Summary());
            Check("a deleted file's copy moves to Older versions",
                  second.Removed == 1 && !File.Exists(Path.Combine(current, "sub", "b.txt")) && File.Exists(Path.Combine(older, "sub", "b.txt")), second.Summary());

            BackupOutcome third = BackupEngine.Run(folders, destination, "id", start.AddMinutes(2), CancellationToken.None);
            Check("nothing changed: nothing copied", third.Finished && third.Added + third.Updated + third.Removed == 0, third.Summary());

            BackupOutcome missing = BackupEngine.Run([documents, Path.Combine(root, "pc", "Gone")], destination, "id", start.AddMinutes(3), CancellationToken.None);
            Check("a missing folder is reported", missing.MissingFolders.Count == 1, missing.Summary());
            Directory.Move(documents, documents + "-away");
            BackupOutcome away = BackupEngine.Run(folders, destination, "id", start.AddMinutes(4), CancellationToken.None);
            Check("a folder that went missing keeps its copy", away.Removed == 0 && File.Exists(Path.Combine(current, "a.txt")), away.Summary());
            Directory.Move(documents + "-away", documents);

            BackupEngine.Run(folders, destination, "id", start.AddDays(31), CancellationToken.None);
            Check("old versions past 30 days are removed", !Directory.Exists(Path.Combine(olderRoot, start.AddMinutes(1).ToString("yyyy-MM-dd HH-mm-ss"))));

            var labels = BackupEngine.Labels([@"C:\Users\me\Docs", @"D:\Work\Docs"]);
            Check("two folders with the same name get different names", labels[0].Label == "Docs" && labels[1].Label == "Docs (D)",
                  string.Join(", ", labels.Select(item => item.Label)));
            Check("the destination is found while it's there", BackupEngine.Resolve(destination, "id") == destination);
        }
        catch (Exception ex)
        {
            Check("ran without errors", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leftover temporary files are harmless.
            }
        }
        return (passed, failed);
    }

    /// <summary>An assistant wired like the app's, with stand-ins for everything outside the routing itself.</summary>
    static AssistantService Build(string setup, string folder, string memoryFixture)
    {
        string memoryFolder = Path.Combine(folder, "memory");
        Directory.CreateDirectory(memoryFolder);
        if (Directory.Exists(memoryFixture))
        {
            foreach (string fact in Directory.GetFiles(memoryFixture, "*.md"))
                File.Copy(fact, Path.Combine(memoryFolder, Path.GetFileName(fact)), overwrite: true);
        }

        var memory = new MemoryStore(memoryFolder);
        var reminders = new FakeReminders();
        var conversations = new ConversationLog(Path.Combine(folder, "conversations"));
        conversations.Add(DateTime.Now.AddDays(-1), "Asked how to revise for a history test.");

        var providers = new List<OpenAiCompatibleProvider>();
        if (setup != "no-cloud")
        {
            var endpoint = new CloudEndpoint("Groq", new Uri("https://cloud.invalid/v1/"), "not-a-real-key", "stand-in-model", "models");
            providers.Add(new OpenAiCompatibleProvider(endpoint, new HttpClient(new FakeCloud(answers: setup != "cloud-down"))));
        }
        var cloud = new CloudChat(providers, new SentLog(Path.Combine(folder, "sent")));

        AssistantService? service = null;
        Func<string> currentUserText = () => service?.CurrentUserText ?? "";
        var tools = new List<ITool>
        {
            new FakeTool("search_files"),
            new FakeTool("get_recent_activity"),
            new FakeTool("look_at_screen"),
            new RememberTool(memory, currentUserText),
            new RecallTool(memory),
            new ForgetTool(memory, currentUserText),
        };

        service = new AssistantService(new FakeLocalModel(), tools, cloud, memory, reminders)
        {
            LocalModelEnabled = setup != "lite",
            CloudOnly = setup == "game",
            Organizer = new OrganizerAnswers(new NotesStore(Path.Combine(folder, "notes.md")), new TodoStore(Path.Combine(folder, "todo.md")), reminders),
            Conversations = conversations,
            Knowledge = new WikipediaLookup(new HttpClient(new FakeWikipedia())),
            Actions = new FakeActions(),
            Shortcuts = new FakeShortcuts(),
        };
        return service;
    }
}

/// <summary>A local model that just chats: it never asks for a tool, like a small model that saw nothing to look up.</summary>
sealed class FakeLocalModel : ILlmProvider
{
    public string Name => "stand-in local model";

    public Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct) =>
        Task.FromResult(new LlmResponse("A reply from the stand-in local model.", []));
}

/// <summary>Cloud chat: one fixed answer, or a server error when it is "down".</summary>
sealed class FakeCloud(bool answers) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!answers)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":{\"message\":\"stand-in cloud is down\"}}", Encoding.UTF8, "application/json"),
            });

        var body = new JsonObject
        {
            ["model"] = "stand-in-model",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "A reply from the stand-in cloud chat." },
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1 },
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>
/// Wikipedia: every search finds one article named after the search words, and its text is about them. So a lookup
/// that is asked for always comes back with something, and the route shows whether one was asked for.
/// </summary>
sealed class FakeWikipedia : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string query = request.RequestUri?.Query ?? "";
        JsonObject body;
        if (Value(query, "srsearch") is { } search)
        {
            body = new JsonObject
            {
                ["query"] = new JsonObject { ["search"] = new JsonArray(new JsonObject { ["title"] = search }) },
            };
        }
        else
        {
            string title = Value(query, "titles") ?? "Article";
            string text = $"{title} is what this article is about. It is a made-up article that the checks use in place of Wikipedia.";
            body = new JsonObject
            {
                ["query"] = new JsonObject { ["pages"] = new JsonArray(new JsonObject { ["title"] = title, ["extract"] = text }) },
            };
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        });
    }

    static string? Value(string query, string name)
    {
        foreach (string part in query.TrimStart('?').Split('&'))
        {
            int equals = part.IndexOf('=');
            if (equals > 0 && part[..equals] == name) return Uri.UnescapeDataString(part[(equals + 1)..].Replace('+', ' '));
        }
        return null;
    }
}

/// <summary>A tool that touches Windows in the app (file search, activity, the screen): here it only says it ran.</summary>
sealed class FakeTool(string name) : ITool
{
    public ToolDefinition Definition { get; } = new(name, "A stand-in for the checks.", new JsonObject { ["type"] = "object" });

    public Task<ToolOutcome> ExecuteAsync(JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(new ToolOutcome($"{Definition.Name} ran.") { Reply = $"{Definition.Name} ran." });
}

/// <summary>
/// The app's actions. Like the real runner it passes on "open X" when X isn't an app, folder or site it knows,
/// so file search can answer ("open the file report.docx").
/// </summary>
sealed class FakeActions : IActionRunner
{
    static readonly string[] Known = ["spotify", "chrome", "notepad", "downloads", "youtube"];

    public Task<ActionReply?> RunAsync(ActionIntent intent, CancellationToken ct)
    {
        if (intent is OpenTarget target && !Known.Any(name => target.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult<ActionReply?>(null);
        return Task.FromResult<ActionReply?>(new ActionReply("Done."));
    }
}

sealed class FakeShortcuts : IShortcutBook
{
    public string Add(string keys, string command) => "Shortcut added.";
    public string Remove(string keys) => "Shortcut removed.";
    public string List() => "No shortcuts yet.";
}

sealed class FakeReminders : IReminders
{
    public bool Start(TimeSpan after, string label) => true;
    public bool At(DateTime due, string label) => true;
    public IReadOnlyList<(string Label, TimeSpan Left)> Running => [];
    public IReadOnlyList<(string Label, DateTime Due, bool Timer)> Upcoming => [];
    public int CancelAll() => 0;
    public IReadOnlyList<string> Cancel(string words) => [];
}
