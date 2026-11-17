using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core;
using Assistant.Core.Actions;
using Assistant.Core.Agenda;
using Assistant.Core.Backup;
using Assistant.Core.Diagnostics;
using Assistant.Core.Guide;
using Assistant.Core.Knowledge;
using Assistant.Core.Llm;
using Assistant.Core.Mail;
using Assistant.Core.Memory;
using Assistant.Core.Organizer;
using Assistant.Core.Storage;
using Assistant.Core.Tools;
using Assistant.Core.Voice;

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
        // --ask: everyday messages through the real cloud chat (see Ask.cs). Not part of the ordinary checks.
        if (args.Contains("--ask")) return await Ask.RunAsync(cases);
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

        (int storagePassed, int storageFailed) = StorageChecks(Path.Combine(temp, "storage"), showAll);
        passed += storagePassed;
        failed += storageFailed;

        (int dayPassed, int dayFailed) = await DayChecks(Path.Combine(temp, "day"), cases, showAll);
        passed += dayPassed;
        failed += dayFailed;

        (int wakePassed, int wakeFailed) = WakeChecks(showAll);
        passed += wakePassed;
        failed += wakeFailed;

        (int guidePassed, int guideFailed) = await GuideChecks(Path.Combine(temp, "guide"), cases, showAll);
        passed += guidePassed;
        failed += guideFailed;

        (int offerPassed, int offerFailed) = await OfferChecks(Path.Combine(temp, "offers"), Path.Combine(cases, "memory"), showAll);
        passed += offerPassed;
        failed += offerFailed;

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

    /// <summary>
    /// Settings that survive a damaged config.json (the last good copy comes back, the damaged file is set aside), and
    /// logged errors that carry no file paths.
    /// </summary>
    static (int Passed, int Failed) StorageChecks(string root, bool showAll)
    {
        int passed = 0, failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok)
            {
                passed++;
                if (showAll) Console.WriteLine($"ok    storage: {name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"FAIL  storage: {name} {detail}");
            }
        }

        try
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "config.json");
            var config = new AppConfig { Performance = "lite" };
            Check("settings save", config.Save(file) && File.Exists(file) && !File.Exists(file + ".tmp"));
            Check("settings load back", AppConfig.Load(file).Performance == "lite");
            Check("a spare copy is kept", File.Exists(file + ".bak"));

            File.WriteAllText(file, "{ \"performance\": \"li"); // cut off mid-write
            AppConfig recovered = AppConfig.Load(file);
            Check("a damaged file falls back to the spare copy", recovered.Performance == "lite", recovered.Performance);
            Check("the damaged file is set aside", File.Exists(Path.Combine(root, "config.broken.json")));
            Check("the settings file is whole again", AppConfig.Load(file).Performance == "lite");

            // (Not storage, but small enough to live here.) The name a factual question is about, for the Wikipedia search.
            Check("a title in a question is found", WikipediaLookup.Name("Who wrote Under the Yoke?") == "Under the Yoke", WikipediaLookup.Name("Who wrote Under the Yoke?") ?? "none");
            Check("a Bulgarian name is found", WikipediaLookup.Name("Кога е роден Христо Ботев?") == "Христо Ботев", WikipediaLookup.Name("Кога е роден Христо Ботев?") ?? "none");
            Check("no name, no extra search", WikipediaLookup.Name("what is photosynthesis?") is null);

            string scrubbed = DiagnosticLog.WithoutPaths(@"Could not find file 'C:\Users\me\Documents\my taxes 2025.pdf'.");
            Check("logged errors carry no file paths", !scrubbed.Contains("taxes") && !scrubbed.Contains("Users"), scrubbed);
            string bare = DiagnosticLog.WithoutPaths(@"The process cannot access the file D:\Docs\CV.pdf because it is in use");
            Check("bare paths are taken out too", !bare.Contains("CV.pdf"), bare);
            Check("ordinary text is left alone", DiagnosticLog.WithoutPaths("Connection refused (localhost:11434) at 12:30") == "Connection refused (localhost:11434) at 12:30");
        }
        catch (Exception ex)
        {
            Check("ran without errors", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        return (passed, failed);
    }

    /// <summary>
    /// Calendar, mail and routines: a made-up calendar file is read right (times, whole days, repeats, skipped dates,
    /// cancelled events), the questions are recognised, a routine runs once a day and catches up the same day, and
    /// through the whole assistant none of it reaches cloud chat.
    /// </summary>
    static async Task<(int Passed, int Failed)> DayChecks(string root, string cases, bool showAll)
    {
        int passed = 0, failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok)
            {
                passed++;
                if (showAll) Console.WriteLine($"ok    day: {name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"FAIL  day: {name} {detail}");
            }
        }

        try
        {
            // Monday 16 November 2026, 08:00.
            var now = new DateTime(2026, 11, 16, 8, 0, 0);
            DateTime monday = now.Date;
            string Titles(DateTime day) => string.Join(" | ", IcsCalendar.Between(FakeCalendar.Ics, day, day.AddDays(1)).Select(item => $"{item.Start:HH:mm} {item.Title}"));

            Check("a day's events, in order, without the cancelled one",
                  Titles(monday) == "09:00 Standup | 14:00 A very long title that is folded onto two lines | 18:00 Gym, legs", Titles(monday));
            Check("a whole-day event is on its day only", Titles(monday.AddDays(1)) == "00:00 Mila's birthday", Titles(monday.AddDays(1)));
            Check("a skipped date of a repeating event stays empty", Titles(monday.AddDays(2)) == "", Titles(monday.AddDays(2)));
            Check("a weekly event repeats on its days", Titles(monday.AddDays(9)) == "18:00 Gym, legs" && Titles(monday.AddDays(8)) == "", Titles(monday.AddDays(9)));

            Check("\"what's on my calendar today?\"", CalendarAnswers.Parse("What's on my calendar today?", now) is { Next: false } today && today.From == monday);
            Check("\"what do I have tomorrow?\"", CalendarAnswers.Parse("what do I have tomorrow?", now) is { Label: "tomorrow" });
            Check("\"when is my next meeting?\"", CalendarAnswers.Parse("when is my next meeting?", now) is { Next: true });
            Check("Bulgarian: \"какво имам утре в календара?\"", CalendarAnswers.Parse("какво имам утре в календара?", now) is { Label: "tomorrow" });
            Check("a weekday means the coming one", CalendarAnswers.Parse("am I free on friday?", now) is { } friday && friday.From == monday.AddDays(4));
            Check("the to-do list isn't the calendar", CalendarAnswers.Parse("what do I have to do?", now) is null && CalendarAnswers.Parse("what's on my mind", now) is null);
            string said = await CalendarAnswers.AnswerAsync(CalendarAnswers.Parse("what's on my calendar today", now)!, new FakeCalendar(), now, CancellationToken.None);
            Check("the answer lists times, titles and places", said.Contains("09:00–09:30: Standup (Room 2)") && said.Contains("18:00–19:00: Gym, legs"), said.Replace("\n", " / "));
            string next = await CalendarAnswers.AnswerAsync(CalendarAnswers.Parse("when is my next meeting", now)!, new FakeCalendar(), now, CancellationToken.None);
            Check("the next thing is the first one still to come", next.StartsWith("Next: Standup, today at 09:00", StringComparison.Ordinal), next);

            // Mail.
            Check("\"any new emails?\" is about unread mail", MailAnswers.Parse("Any new emails?") is { From: "", Explicit: true });
            Check("\"did Ivan reply?\" names someone", MailAnswers.Parse("did Ivan reply?") is { From: "Ivan", Explicit: false });
            Check("\"emails from the bank\"", MailAnswers.Parse("any emails from the bank?") is { From: "the bank", Explicit: true });
            Check("Bulgarian: \"писа ли ми Иван?\"", MailAnswers.Parse("писа ли ми Иван?") is { From: "Иван" });
            Check("\"did it reply\" isn't about a person", MailAnswers.Parse("did it reply?") is null && MailAnswers.Parse("what did I do today") is null);
            Check("the mail server is worked out from the address",
                  ImapMailbox.Host("me@gmail.com", "") == "imap.gmail.com" && ImapMailbox.Host("me@abv.bg", "") == "imap.abv.bg"
                  && ImapMailbox.Host("me@school.example", "") == "imap.school.example" && ImapMailbox.Host("me@gmail.com", " mail.own.example ") == "mail.own.example");

            // Routines.
            Directory.CreateDirectory(root);
            var routines = new RoutineStore(Path.Combine(root, "routines.json"));
            DateTime seven = monday.AddHours(7);
            string? added = RoutineAnswers.TryAnswer("Every morning at 8 tell me what's on my calendar today", routines, seven);
            Check("a routine is saved with its time and its question",
                  added is not null && routines.All() is [{ Hour: 8, Minute: 0, Prompt: "what's on my calendar today", Days.Count: 0 }], added ?? "not recognised");
            Check("not before its time", routines.TakeDue(monday.AddHours(7.5)).Count == 0);
            Check("at its time, once", routines.TakeDue(monday.AddHours(8)).Count == 1 && routines.TakeDue(monday.AddHours(8.1)).Count == 0);
            Check("missed in the morning, it runs when the PC is back that day", routines.TakeDue(monday.AddDays(1).AddHours(10)).Count == 1);
            RoutineAnswers.TryAnswer("every weekday at 9: any new emails?", routines, monday.AddDays(1).AddHours(11));
            Check("added after its time, it starts the next day", routines.TakeDue(monday.AddDays(1).AddHours(12)).Count == 0 && routines.TakeDue(monday.AddDays(2).AddHours(9)).Count == 2);
            Check("a weekday routine rests at the weekend", routines.TakeDue(monday.AddDays(5).AddHours(12)).Count == 1);
            Check("\"every day\" in a sentence isn't a routine", RoutineAnswers.TryAnswer("every day 5 minutes of stretching is healthy", routines, seven) is null);
            Check("routines can be listed and removed",
                  RoutineAnswers.TryAnswer("my routines", routines, seven)?.Contains("2. every weekday at 09:00: any new emails") == true
                  && RoutineAnswers.TryAnswer("delete routine 1", routines, seven) is not null && routines.All().Count == 1);
            Check("Bulgarian: \"всеки ден в 8 …\"", RoutineAnswers.TryAnswer("всеки ден в 8:30 кажи ми какво имам днес", routines, seven) is not null
                  && routines.All()[^1] is { Hour: 8, Minute: 30, Prompt: "какво имам днес" }, routines.All()[^1].ToString());

            Check("cloud chat's mail button is answered from the inbox",
                  ActionCatalog.FromCall(new ToolCall("check_mail", JsonDocument.Parse("{}").RootElement.Clone())) is CheckMail { From: "" });
            Check("cloud chat's calendar button", ActionCatalog.FromCall(new ToolCall("check_calendar", JsonDocument.Parse("""{"when":"tomorrow"}""").RootElement.Clone())) is CheckCalendar { Day: "tomorrow" });

            // Through the whole assistant: answered here, and none of it in later cloud chat.
            AssistantService assistant = Build("normal", Path.Combine(root, "1"), Path.Combine(cases, "memory"));
            AssistantReply reply = await assistant.SendAsync("did Ivan reply?", CancellationToken.None);
            Check("mail is answered by the app", reply.Route == "mail" && reply.Text.Contains("Re: Project") && reply.Source == "local", $"{reply.Route}: {reply.Text}");
            AssistantReply pressed = await assistant.RunActionAsync(new CheckMail(), CancellationToken.None);
            Check("pressing the mail button lists the inbox", pressed.Route == "mail" && pressed.Text.Contains("Re: Project"), pressed.Text);
            reply = await assistant.SendAsync("what's on my calendar today?", CancellationToken.None);
            Check("so is the calendar", reply.Route == "calendar" && reply.Source == "local", reply.Route);
            FakeCloud.LastRequest = "";
            await assistant.SendAsync("thanks!", CancellationToken.None);
            Check("neither reaches cloud chat afterwards",
                  FakeCloud.LastRequest.Length > 0 && !FakeCloud.LastRequest.Contains("Re: Project") && !FakeCloud.LastRequest.Contains("Standup"), "a subject or a title was in the request");

            AssistantService alone = Build("normal", Path.Combine(root, "2"), Path.Combine(cases, "memory"), inbox: new FakeMailbox(connected: false));
            reply = await alone.SendAsync("did Ivan reply?", CancellationToken.None);
            Check("without a mailbox, \"did Ivan reply?\" is ordinary chat", reply.Route != "mail", reply.Route);
            reply = await alone.SendAsync("any new emails?", CancellationToken.None);
            Check("and \"any new emails?\" says how to connect one", reply.Route == "mail" && reply.Text.Contains("No mailbox is connected"), reply.Text);
        }
        catch (Exception ex)
        {
            Check("ran without errors", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        return (passed, failed);
    }

    /// <summary>
    /// The wake word's bookkeeping, with stand-ins for its three models: sound is cut into 80 ms steps however it
    /// arrives, each model gets the amount it expects, nothing fires before 1.3 s of real sound or twice in a row,
    /// and Reset forgets everything. (Whether the real models hear the phrase can only be tried with a microphone.)
    /// </summary>
    static (int Passed, int Failed) WakeChecks(bool showAll)
    {
        int passed = 0, failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok)
            {
                passed++;
                if (showAll) Console.WriteLine($"ok    wake word: {name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"FAIL  wake word: {name} {detail}");
            }
        }

        try
        {
            int spectrograms = 0, embeddings = 0, scores = 0;
            bool sizesRight = true;
            float sure = 0f;
            var detector = new WakeWordDetector(
                samples =>
                {
                    spectrograms++;
                    sizesRight &= samples.Length == WakeWordDetector.Chunk + WakeWordDetector.Overlap;
                    return new float[8 * WakeWordDetector.Bands];
                },
                strips =>
                {
                    embeddings++;
                    sizesRight &= strips.Length == WakeWordDetector.Strips * WakeWordDetector.Bands && strips[^1] == 2f; // 0 / 10 + 2
                    return new float[WakeWordDetector.EmbeddingSize];
                },
                last =>
                {
                    scores++;
                    sizesRight &= last.Length == 16 * WakeWordDetector.EmbeddingSize;
                    return sure;
                });

            // Odd-sized pieces, as a sound card delivers them: 10 chunks' worth in pieces of 1000 samples.
            var piece = new short[1000];
            bool fired = false;
            for (int sent = 0; sent < 10 * WakeWordDetector.Chunk; sent += piece.Length) fired |= detector.Feed(piece);
            Check("sound is cut into 80 ms steps however it arrives", spectrograms == 10 && embeddings == 10, $"{spectrograms} steps");
            Check("nothing is scored before 1.3 s of sound", scores == 0 && !fired, $"{scores} scores");

            var chunk = new short[WakeWordDetector.Chunk];
            for (int i = 0; i < 10; i++) fired |= detector.Feed(chunk);
            Check("each model gets the amount of sound it expects", sizesRight);
            Check("an unsure score doesn't wake it", scores > 0 && !fired, $"{scores} scores");

            sure = 0.9f;
            Check("a sure score wakes it", detector.Feed(chunk));
            Check("not twice for the same phrase", !detector.Feed(chunk) && !detector.Feed(chunk));
            int fires = 0;
            for (int i = 0; i < 30; i++) fires += detector.Feed(chunk) ? 1 : 0;
            Check("again once two seconds have passed", fires == 1, $"{fires} in 2.4 s");

            detector.Reset();
            scores = 0;
            for (int i = 0; i < 15; i++) fired = detector.Feed(chunk);
            Check("after a reset it listens from scratch", scores == 0);
        }
        catch (Exception ex)
        {
            Check("ran without errors", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        return (passed, failed);
    }

    /// <summary>
    /// Show me where to click: which messages ask for it, which control a name means (on a made-up window,
    /// cases\windows\editor.json), and through the whole assistant: the control is marked and never clicked, a task
    /// goes to the local model with the names, and cloud chat only gets the names left on the card.
    /// </summary>
    static async Task<(int Passed, int Failed)> GuideChecks(string root, string cases, bool showAll)
    {
        int passed = 0, failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok)
            {
                passed++;
                if (showAll) Console.WriteLine($"ok    guide: {name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"FAIL  guide: {name} {detail}");
            }
        }

        try
        {
            // Which messages are for the guide.
            GuideAsk? save = GuideRequest.Parse("Where is the save button?");
            Check("\"where is the save button\" names a control", save is { Named: true, What: "save", Kind: "button" }, save?.ToString() ?? "not recognised");
            GuideAsk? font = GuideRequest.Parse("where do I click to change the font?");
            Check("\"where do I click to…\" is a task", font is { Named: false, What: "change the font" }, font?.ToString() ?? "not recognised");
            GuideAsk? bigger = GuideRequest.Parse("How do I make the text bigger in this app?");
            Check("\"how do I … in this app\" is a task", bigger is { Named: false, What: "make the text bigger" }, bigger?.ToString() ?? "not recognised");
            GuideAsk? zapazi = GuideRequest.Parse("къде е бутонът за запазване?");
            Check("Bulgarian: a control by name", zapazi is { Named: true, What: "запазване" }, zapazi?.ToString() ?? "not recognised");
            GuideAsk? kude = GuideRequest.Parse("Покажи ми къде да натисна, за да сменя шрифта");
            Check("Bulgarian: a task", kude is { Named: false, What: "сменя шрифта" }, kude?.ToString() ?? "not recognised");
            foreach (string other in new[] { "where is my tax report", "how do I bake bread?", "show me the stuff I downloaded", "what does this error mean", "where is Plovdiv?" })
                Check($"not for the guide: {other}", GuideRequest.Parse(other) is null);

            GuideAsk? inApp = GuideRequest.Parse("how do I make a table in Excel?");
            Check("\"… in Excel\" names the app", inApp is { Named: false, What: "make a table", App: "Excel" }, inApp?.ToString() ?? "not recognised");
            Check("the app named has to be the one in use", GuideRequest.SameApp("excel", "Excel") && GuideRequest.SameApp("excel", "Microsoft Excel") && !GuideRequest.SameApp("Bulgaria", "Excel"));

            // Which control a name means.
            UiWindow window = FakeGuide.Load(cases);
            string Best(string wanted, string kind = "") => GuideMatch.Best(GuideMatch.Find(wanted, window.Elements, kind))?.Name ?? "(none)";
            Check("save is Save, not Save as", Best("save") == "Save", Best("save"));
            Check("save as", Best("save as") == "Save as…", Best("save as"));
            Check("a shortcut after the name doesn't matter", Best("print") == "Print\tCtrl+P" && Best("bold") == "Bold (Ctrl+B)", Best("print") + ", " + Best("bold"));
            Check("the menu's underline mark doesn't matter", Best("format") == "F&ormat", Best("format"));
            Check("one typo is fine", Best("setings") == "Settings", Best("setings"));
            Check("the Bulgarian word finds the English button", Best("запазване") == "Save" && Best("настройки") == "Settings", Best("запазване") + ", " + Best("настройки"));
            Check("two that fit equally aren't guessed", Best("zoom") == "(none)" && GuideMatch.Find("zoom", window.Elements).Count == 2, Best("zoom"));
            Check("the kind named decides between two", GuideMatch.Best(GuideMatch.Find("share", window.Elements, "link"))?.Kind == "link" && Best("share") == "(none)");
            Check("plain text isn't something to click", Best("ln 1") == "(none)");
            Check("nothing fits nonsense", GuideMatch.Find("flux capacitor", window.Elements).Count == 0);
            UiElement settings = window.Elements.First(element => element.Name == "Settings");
            Check("where it is, in words", GuideMatch.Describe(settings, window) == "\"Settings\" (button, top right)", GuideMatch.Describe(settings, window));
            string listing = GuideMatch.Listing(window, 200);
            Check("the list for a model has things to click only", listing.Contains("3. Format (menu item)") && !listing.Contains("Ln 1") && !listing.Contains("Paste"), listing.Replace("\n", " | "));
            Check("a line of the list leads back to its control", GuideMatch.FromListing(listing, 3, window)?.Name == "F&ormat");

            // Through the whole assistant.
            var guide = new FakeGuide(cases);
            AssistantService assistant = Build("normal", Path.Combine(root, "1"), Path.Combine(cases, "memory"), guide: guide);
            AssistantReply reply = await assistant.SendAsync("where is the save button?", CancellationToken.None);
            Check("a named control is ringed", reply.Route == "guide" && guide.Shown?.Name == "Save", $"route {reply.Route}, shown {guide.Shown?.Name}");
            Check("\"I can't see it\" is offered", reply.Options.Any(option => option.Intent is PointAtElement), string.Join(", ", reply.Options.Select(option => option.Label)));
            if (reply.Options.FirstOrDefault(option => option.Intent is PointAtElement)?.Intent is { } cantSee)
                await assistant.RunActionAsync(cantSee, CancellationToken.None);
            Check("then the character goes there", guide.Pointed?.Name == "Save");

            // Offered by cloud chat (show_where): pressing the button finds and rings it.
            Check("cloud chat's \"show where\" is a button like any other",
                  ActionCatalog.Offers([new ToolCall("show_where", JsonDocument.Parse("""{"what":"print"}""").RootElement.Clone())]) is [{ Intent: GuideShow { What: "print" } }]);
            guide.Shown = null;
            await assistant.RunActionAsync(new GuideShow("print"), CancellationToken.None);
            Check("pressing it rings the control", guide.Shown?.Name == "Print\tCtrl+P", guide.Shown?.Name ?? "nothing");

            // Typed instead of pressed.
            var typed = new FakeGuide(cases);
            AssistantService typing = Build("normal", Path.Combine(root, "4"), Path.Combine(cases, "memory"), guide: typed);
            AssistantReply early = await typing.SendAsync("I don't see it", CancellationToken.None);
            Check("\"I don't see it\" with nothing marked is ordinary chat", early.Route != "guide" && typed.Pointed is null, early.Route);
            await typing.SendAsync("where is the save button?", CancellationToken.None);
            AssistantReply after = await typing.SendAsync("I don't see it", CancellationToken.None);
            Check("typed \"I don't see it\" sends the character over", after.Route == "guide" && typed.Pointed?.Name == "Save", after.Route);
            foreach (string said in new[] { "go to it", "I still don't see anything, point at it", "show me where it is", "where??", "иди при него" })
            {
                after = await typing.SendAsync(said, CancellationToken.None);
                Check($"after a ring, \"{said}\" is about the ring", after.Route == "guide", after.Route);
            }
            after = await typing.SendAsync("where is the print button?", CancellationToken.None);
            Check("a new question after a ring is a new question", after.Route == "guide" && typed.Shown?.Name == "Print\tCtrl+P", typed.Shown?.Name ?? "nothing");
            after = await typing.SendAsync("не го виждам", CancellationToken.None);
            Check("so does \"не го виждам\"", after.Route == "guide", after.Route);
            await typing.SendAsync("where do I click to change the font?", CancellationToken.None);
            int looks = typed.Scans;
            after = await typing.SendAsync("next", CancellationToken.None);
            Check("typed \"next\" looks at the window again", after.Route == "guide" && typed.Scans == looks + 1, $"{after.Route}, {typed.Scans - looks} looks");

            FakeCloud.LastRequest = "";
            await assistant.SendAsync("thanks!", CancellationToken.None);
            Check("names of controls stay off later cloud chat", FakeCloud.LastRequest.Length > 0 && !FakeCloud.LastRequest.Contains("(button"), "the guide's answer was in the request");

            reply = await assistant.SendAsync("where is the zoom button", CancellationToken.None);
            Check("two that fit become a choice", reply.Options.Count(option => option.Intent is ShowElement) == 2, string.Join(", ", reply.Options.Select(option => option.Label)));

            guide.Shown = null;
            reply = await assistant.SendAsync("where do I click to change the font?", CancellationToken.None);
            Check("a task: the local model picks where to start", guide.Shown?.Name == "F&ormat" && reply.Options.Any(option => option.Intent is GuideNextStep),
                  $"shown {guide.Shown?.Name ?? "nothing"}");
            Check("nothing went to the cloud for it", reply.GuideCloud is null && reply.Source == "local");

            // Lite has no local model: cloud chat is offered, and gets only what's left on the card.
            var liteGuide = new FakeGuide(cases);
            AssistantService lite = Build("lite", Path.Combine(root, "2"), Path.Combine(cases, "memory"), guide: liteGuide);
            FakeCloud.LastRequest = "";
            reply = await lite.SendAsync("where do I click to change the font?", CancellationToken.None);
            Check("without a local model, cloud chat is offered and nothing is sent yet",
                  reply.GuideCloud is not null && liteGuide.Shown is null && FakeCloud.LastRequest.Length == 0, $"offer {reply.GuideCloud is not null}");
            if (reply.GuideCloud is { } offer)
            {
                string kept = string.Join("\n", offer.Names.Split('\n').Where(line => !line.Contains("Search")));
                AssistantReply picked = await lite.GuideWithCloudAsync(offer.Task, kept, CancellationToken.None);
                Check("after Send, cloud chat's pick is ringed", liteGuide.Shown?.Name == "F&ormat", $"shown {liteGuide.Shown?.Name ?? "nothing"}: {picked.Text}");
                Check("a line taken off the card isn't sent", FakeCloud.LastRequest.Contains("Format") && !FakeCloud.LastRequest.Contains("Search"));
            }

            var gameGuide = new FakeGuide(cases);
            AssistantService game = Build("game", Path.Combine(root, "3"), Path.Combine(cases, "memory"), guide: gameGuide);
            reply = await game.SendAsync("where is the save button?", CancellationToken.None);
            Check("in game mode no window is looked at", reply.Route == "guide" && gameGuide.Scans == 0 && gameGuide.Shown is null, $"{gameGuide.Scans} looks");
        }
        catch (Exception ex)
        {
            Check("ran without errors", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        return (passed, failed);
    }

    /// <summary>
    /// Chat that acts: what cloud chat asks for becomes a button and nothing more. The catalogue turns good calls into
    /// intents and drops bad ones, a reply with a call carries buttons, nothing was run to get there, and the list of
    /// actions stays small.
    /// </summary>
    static async Task<(int Passed, int Failed)> OfferChecks(string root, string memoryFixture, bool showAll)
    {
        int passed = 0, failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok)
            {
                passed++;
                if (showAll) Console.WriteLine($"ok    offers: {name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"FAIL  offers: {name} {detail}");
            }
        }
        static ToolCall Call(string name, string json) => new(name, JsonDocument.Parse(json).RootElement.Clone());

        try
        {
            Check("volume set 30", ActionCatalog.FromCall(Call("volume", """{"change":"set","percent":30}""")) is VolumeChange { Op: VolumeOp.Set, Percent: 30 });
            Check("a number sent as text still counts", ActionCatalog.FromCall(Call("volume", """{"change":"set","percent":"45%"}""")) is VolumeChange { Percent: 45 });
            Check("volume 500 is dropped", ActionCatalog.FromCall(Call("volume", """{"change":"set","percent":500}""")) is null);
            Check("an unknown action is dropped", ActionCatalog.FromCall(Call("format_disk", "{}")) is null);
            Check("a missing parameter is dropped", ActionCatalog.FromCall(Call("open", "{}")) is null);
            Check("a parameter of the wrong kind is dropped", ActionCatalog.FromCall(Call("open", """{"name":["a","b"]}""")) is null);
            Check("open by name", ActionCatalog.FromCall(Call("open", """{"name":" Spotify ","kind":"app"}""")) is OpenTarget { Name: "Spotify", Kind: OpenKind.App });
            Check("a settings page by its word", ActionCatalog.FromCall(Call("open_settings", """{"page":"Bluetooth"}""")) is OpenSettings { Link: "ms-settings:bluetooth" });
            Check("an unknown settings page is dropped", ActionCatalog.FromCall(Call("open_settings", """{"page":"registry"}""")) is null);
            Check("a theme the user didn't name means the other one", ActionCatalog.FromCall(Call("set_theme", """{"mode":"other"}""")) is SwitchTheme);
            Check("shutting down still asks first", ActionCatalog.FromCall(Call("power", """{"what":"shut_down"}""")) is PowerOff { Restart: false, Confirmed: false });
            Check("cleaning up still asks first", ActionCatalog.FromCall(Call("clean_up", """{"what":"temporary_files"}""")) is CleanUp { Confirmed: false });
            Check("emptying the bin still asks first", ActionCatalog.FromCall(Call("empty_recycle_bin", "{}")) is EmptyRecycleBin { Confirmed: false });

            bool allLabelled = true;
            foreach (CatalogAction action in ActionCatalog.All)
            {
                // Every action must be reachable: its name unique, and its description short.
                if (ActionCatalog.All.Count(other => other.Name == action.Name) != 1 || action.Description.Length > 260) allLabelled = false;
            }
            Check("every action has one name and a short description", allLabelled);
            var offered = ActionCatalog.Offers([Call("lock_pc", "{}"), Call("lock_pc", "{}"), Call("nope", "{}"), Call("speed_test", "{}"),
                                                Call("take_screenshot", "{}"), Call("backup", """{"what":"status"}""")]);
            Check("at most three buttons, no repeats, bad calls left out",
                  offered.Count == ActionCatalog.MaxOffers && offered[0].Label == "Lock the PC" && offered[1].Intent is SpeedTest,
                  string.Join(", ", offered.Select(option => option.Label)));
            int size = ActionCatalog.Definitions.Sum(definition => definition.Name.Length + definition.Description.Length + definition.Parameters.ToJsonString().Length);
            Check("the list of actions stays small (under 6,000 characters)", size < 6000, $"{size} characters");

            // Through the whole assistant: the stand-in cloud asks for "volume up", and the app must offer it, not do it.
            var actions = new FakeActions();
            AssistantService assistant = Build("normal", Path.Combine(root, "1"), memoryFixture, actions);
            AssistantReply reply = await assistant.SendAsync("it's way too quiet in here, sort it out", CancellationToken.None);
            Check("a call from cloud chat becomes a button", reply.Options.Count == 1 && reply.Options[0].Intent is VolumeChange { Op: VolumeOp.Up },
                  $"route {reply.Route}, {reply.Options.Count} buttons");
            Check("the button's label is the app's own", reply.Options.Count == 1 && reply.Options[0].Label == "Volume up");
            Check("nothing ran before the button was pressed", actions.Ran == 0, $"{actions.Ran} ran");
            if (reply.Options.Count == 1 && reply.Options[0].Intent is { } pressed) await assistant.RunActionAsync(pressed, CancellationToken.None);
            Check("pressing the button runs it once", actions.Ran == 1, $"{actions.Ran} ran");

            // What the action did must not go to the cloud with the next message.
            FakeCloud.LastRequest = "";
            await assistant.SendAsync("thanks!", CancellationToken.None);
            Check("what the action did stays off the next cloud request", FakeCloud.LastRequest.Length > 0 && !FakeCloud.LastRequest.Contains("Done."),
                  "the action's reply was in the request");

            var off = Build("normal", Path.Combine(root, "2"), memoryFixture);
            off.OfferActions = false;
            AssistantReply plain = await off.SendAsync("it's way too quiet in here, sort it out", CancellationToken.None);
            Check("with offers off, the list isn't sent", plain.Route == "cloud" && plain.Options.Count == 0, plain.Route);
        }
        catch (Exception ex)
        {
            Check("ran without errors", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        return (passed, failed);
    }

    /// <summary>An assistant wired like the app's, with stand-ins for everything outside the routing itself.</summary>
    internal static AssistantService Build(string setup, string folder, string memoryFixture, FakeActions? actions = null,
                                           CloudChat? realCloud = null, WikipediaLookup? realWikipedia = null, FakeGuide? guide = null,
                                           FakeMailbox? inbox = null)
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
        CloudChat cloud = realCloud ?? new CloudChat(providers, new SentLog(Path.Combine(folder, "sent")));

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
            Knowledge = realWikipedia ?? new WikipediaLookup(new HttpClient(new FakeWikipedia())),
            Actions = actions ?? new FakeActions(),
            Guide = guide ?? new FakeGuide(Path.GetDirectoryName(memoryFixture) ?? ""),
            Routines = new RoutineStore(Path.Combine(folder, "routines.json")),
            Agenda = new FakeCalendar(),
            Inbox = inbox ?? new FakeMailbox(connected: true),
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
        Task.FromResult(new LlmResponse(GuidePick.Answer(messages.LastOrDefault()?.Content ?? "") ?? "A reply from the stand-in local model.", []));
}

/// <summary>
/// What a model would answer when the guide asks where to click first: for anything about the font, the number of
/// the "Format" line in the list it was given. Null when the message isn't that question.
/// </summary>
static class GuidePick
{
    public static string? Answer(string message)
    {
        if (!message.Contains("Controls:", StringComparison.Ordinal)) return null;
        if (!message.Contains("font", StringComparison.OrdinalIgnoreCase)) return "0";
        foreach (string line in message.Split('\n'))
        {
            int dot = line.IndexOf(". Format (", StringComparison.Ordinal);
            if (dot > 0) return line[..dot].Trim();
        }
        return "0";
    }
}

/// <summary>A made-up calendar, as the .ics text a calendar link would give.</summary>
sealed class FakeCalendar : ICalendarSource
{
    public const string Ics = """
        BEGIN:VCALENDAR
        BEGIN:VEVENT
        SUMMARY:Standup
        DTSTART:20261116T090000
        DTEND:20261116T093000
        LOCATION:Room 2
        END:VEVENT
        BEGIN:VEVENT
        SUMMARY:Mila's birthday
        DTSTART;VALUE=DATE:20261117
        DTEND;VALUE=DATE:20261118
        END:VEVENT
        BEGIN:VEVENT
        SUMMARY:Gym\, legs
        DTSTART:20261102T180000
        DTEND:20261102T190000
        RRULE:FREQ=WEEKLY;BYDAY=MO,WE
        EXDATE:20261118T180000
        END:VEVENT
        BEGIN:VEVENT
        SUMMARY:Cancelled thing
        STATUS:CANCELLED
        DTSTART:20261116T120000
        END:VEVENT
        BEGIN:VEVENT
        SUMMARY:A very long title that is
          folded onto two lines
        DTSTART:20261116T140000
        END:VEVENT
        END:VCALENDAR
        """;

    public bool Connected => true;

    public Task<IReadOnlyList<CalendarEvent>> BetweenAsync(DateTime from, DateTime to, CancellationToken ct) =>
        Task.FromResult(IcsCalendar.Between(Ics, from, to));
}

/// <summary>A made-up inbox: two messages from Ivan, one of them unread.</summary>
sealed class FakeMailbox(bool connected) : IMailbox
{
    static readonly MailLine[] Lines =
    [
        new("Ivan Petrov", "Re: Project", DateTime.Today.AddHours(9)),
        new("Ivan Petrov", "Lunch on Friday?", DateTime.Today.AddDays(-2).AddHours(13)),
    ];

    public bool Connected => connected;
    public Task<MailFound> UnreadAsync(CancellationToken ct) => Task.FromResult(new MailFound([Lines[0]], 1));
    public Task<MailFound> FromAsync(string name, int days, CancellationToken ct) => Task.FromResult(new MailFound(Lines, 2));
}

/// <summary>The guide's eyes and ring: a made-up window from cases\windows\editor.json, and a note of what was marked.</summary>
sealed class FakeGuide(string cases) : IScreenGuide
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public int Scans { get; private set; }
    public UiElement? Shown { get; set; }
    public UiElement? Pointed { get; private set; }

    public static UiWindow Load(string cases) =>
        JsonSerializer.Deserialize<UiWindow>(File.ReadAllText(Path.Combine(cases, "windows", "editor.json")), Json)
        ?? throw new InvalidDataException("editor.json is empty");

    public string WindowApp => "Editor";

    public Task<UiScan> ScanAsync(CancellationToken ct)
    {
        Scans++;
        string file = Path.Combine(cases, "windows", "editor.json");
        return Task.FromResult(File.Exists(file) ? new UiScan(Load(cases), null) : new UiScan(null, "No window to look at."));
    }

    public void Show(UiElement element) => Shown = element;
    public void Point(UiElement element) => Pointed = element;
}

/// <summary>
/// Cloud chat: one fixed answer, or a server error when it is "down". When the request carries the list of actions and
/// the latest message says "sort it out" ("оправи го"), it asks for "volume up" instead, the way a real model would for
/// a request no rule recognised; for "do the impossible" it asks for an action that doesn't exist, next to its answer.
/// </summary>
sealed class FakeCloud(bool answers) : HttpMessageHandler
{
    /// <summary>The body of the last request, so a check can look at what would have left the computer.</summary>
    public static string LastRequest { get; set; } = "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!answers)
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":{\"message\":\"stand-in cloud is down\"}}", Encoding.UTF8, "application/json"),
            };

        string sent = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        LastRequest = sent;
        string latest = "";
        bool withActions = false;
        if (sent.Length > 0)
        {
            using JsonDocument document = JsonDocument.Parse(sent);
            withActions = document.RootElement.TryGetProperty("tools", out JsonElement tools) && tools.GetArrayLength() > 0
                          && tools[0].TryGetProperty("function", out _);
            foreach (JsonElement message in document.RootElement.GetProperty("messages").EnumerateArray())
            {
                if (message.GetProperty("role").GetString() == "user") latest = message.GetProperty("content").GetString() ?? "";
            }
        }

        var reply = new JsonObject { ["role"] = "assistant", ["content"] = GuidePick.Answer(latest) ?? "A reply from the stand-in cloud chat." };
        bool wantsVolume = latest.Contains("sort it out", StringComparison.OrdinalIgnoreCase) || latest.Contains("оправи го", StringComparison.OrdinalIgnoreCase);
        bool wantsNonsense = latest.Contains("do the impossible", StringComparison.OrdinalIgnoreCase);
        if (withActions && (wantsVolume || wantsNonsense))
        {
            if (wantsVolume) reply["content"] = null;
            reply["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = "call_1",
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = wantsVolume ? "volume" : "format_disk",
                    ["arguments"] = wantsVolume ? "{\"change\":\"up\"}" : "{}",
                },
            });
        }

        var body = new JsonObject
        {
            ["model"] = "stand-in-model",
            ["choices"] = new JsonArray(new JsonObject { ["message"] = reply }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1 },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
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

    /// <summary>How many actions were really carried out.</summary>
    public int Ran { get; private set; }

    /// <summary>The last one carried out.</summary>
    public ActionIntent? Last { get; private set; }

    public Task<ActionReply?> RunAsync(ActionIntent intent, CancellationToken ct)
    {
        if (intent is OpenTarget target && !Known.Any(name => target.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult<ActionReply?>(null);
        Ran++;
        Last = intent;
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
