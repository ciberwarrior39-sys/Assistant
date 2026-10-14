using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Assistant.Core;
using Assistant.Core.Activity;
using Assistant.Core.Diagnostics;
using Assistant.Core.Llm;
using Assistant.Core.Memory;
using Assistant.Core.Search;
using Assistant.Core.Storage;
using Assistant.Core.Tools;

namespace Assistant.App;

public partial class App : Application
{
    Mutex? singleInstance;
    System.Windows.Forms.NotifyIcon? tray;
    ActivityTracker? tracker;
    Action? reviewNow; // set once the review is wired up, used by the tray menu

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Launching the app twice must not create a second assistant.
        singleInstance = new Mutex(true, @"Local\Assistant.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            singleInstance.Dispose();
            singleInstance = null;
            MessageBox.Show("The assistant is already running.", "Assistant");
            Shutdown();
            return;
        }

        // The windows hide instead of closing, so the app ends only through Quit.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandledException;
        DiagnosticLog.Info("Starting");

        DataFolder data = DataFolder.Default();
        data.EnsureCreated();
        AppConfig config = AppConfig.Load(data.ConfigFile);
        Retention.Purge(data, Retention.RawLogs);

        var exclusions = new Exclusions(data.ExclusionsFile);
        var activity = new ActivityTracker(new ActivityLog(data.ActivityFolder), exclusions)
        {
            Enabled = config.ActivityLogging,
        };
        tracker = activity;

        // Development uses the Ollama already installed; the bundled one arrives with the installer (step 10).
        var ollama = new HttpClient
        {
            BaseAddress = new Uri(config.OllamaUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(2),
        };
        var fileSearch = new EverythingSearch();

        // Optional cloud chat, tried in this order. A provider without a key is skipped.
        CloudEndpoint[] cloudEndpoints =
        [
            new("Groq", new Uri("https://api.groq.com/openai/v1/"), config.GroqApiKey, config.GroqModel, "models")
            {
                // Groq's production chat models, best for conversation first. Used when the configured
                // model is retired or not allowed for this key.
                FallbackModels = ["llama-3.3-70b-versatile", "openai/gpt-oss-120b", "openai/gpt-oss-20b", "llama-3.1-8b-instant"],
            },
            new("OpenRouter", new Uri("https://openrouter.ai/api/v1/"), config.OpenRouterApiKey, config.OpenRouterModel, "key"),
        ];
        var cloudHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var cloud = new CloudChat(
            cloudEndpoints.Where(endpoint => endpoint.HasKey)
                .Select(endpoint => new OpenAiCompatibleProvider(endpoint, cloudHttp))
                .ToList(),
            new SentLog(data.SentFolder));

        var memory = new MemoryStore(data.MemoryFolder);
        var localModel = new OllamaProvider(ollama, config.Model, config.ContextSize);
        AssistantService? service = null; // the memory tools check the message being answered
        Func<string> currentUserText = () => service?.CurrentUserText ?? "";
        var assistant = new AssistantService(
            localModel,
            [
                new SearchFilesTool(fileSearch),
                new RecentActivityTool(activity),
                new RememberTool(memory, currentUserText),
                new RecallTool(memory),
                new ForgetTool(memory, currentUserText),
            ],
            cloud,
            memory);
        service = assistant;
        var dependencies = new DependencyService(ollama, config.Model, fileSearch, cloudEndpoints, cloudHttp);

        var mood = new CharacterMood();
        var bubble = new ChatBubbleWindow(assistant, dependencies, data, mood);
        var character = new CharacterWindow(mood, bubble, Path.Combine(data.Root, "placement.json"), data.Root, Shutdown);
        MainWindow = character;
        character.Show();

        tray = CreateTrayIcon(character, data.Root);

        // The silent hand-raise, from the same snapshots as the activity log.
        var handRaiser = new HandRaiser(mood, bubble, character, new StruggleDetector(exclusions),
                                        new FeedbackLog(data.FeedbackFolder), config, data.ConfigFile);

        // Game mode: free the GPU, hide, and answer through the cloud while a game is in front.
        var gameMode = new GameModeDetector(config.GameExecutables);
        gameMode.Changed += async on =>
        {
            DiagnosticLog.Info(on ? "Game mode on" : "Game mode off");
            character.SetGameMode(on);
            assistant.CloudOnly = on;
            handRaiser.Paused = on;
            if (on) handRaiser.LowerNow();
            if (on) await localModel.UnloadAsync(CancellationToken.None);
        };

        // One look at the foreground window per second feeds both the activity log and game mode.
        var watcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        watcher.Tick += (_, _) =>
        {
            ForegroundSnapshot snapshot = Desktop.Capture();
            activity.Observe(snapshot);
            if (config.GameModeEnabled) gameMode.Observe(snapshot);
            handRaiser.Observe(snapshot);
        };
        watcher.Start();

        // Weekly review: once a week, facts noticed in the activity log wait for Keep or Forget.
        var review = new ReviewSchedule(data.ReviewFile);
        void CheckReview(bool now = false)
        {
            DateTime today = DateTime.Today;
            if (review.Expire(today)) bubble.ExpireReview();
            if (bubble.HasOpenReview) return;

            IReadOnlyList<ReviewCandidate> candidates = review.StartIfDue(today, declined =>
                WeeklyReview.Propose(activity.Read(today.AddDays(-6), DateTime.Now), memory, declined), now);
            if (now && candidates.Count == 0)
                bubble.Say("Nothing to review yet. I need a few days of activity first (an app used 3+ hours on 3+ days, for example).");
            bubble.ShowReview(candidates, (candidate, keptText) =>
            {
                if (keptText is not null) memory.Add(candidate.Kind, keptText, $"weekly review, {candidate.Evidence}");
                review.Decide(candidate, keptText is not null, DateTime.Today);
            });
        }
        CheckReview();
        reviewNow = () =>
        {
            character.ShowAgain();
            CheckReview(now: true);
            character.OpenChat();
        };

        // Every 6 hours: raw logs older than 7 days go (also at startup, above), and the review is checked.
        var purge = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        purge.Tick += (_, _) =>
        {
            Retention.Purge(data, Retention.RawLogs);
            CheckReview();
        };
        purge.Start();
    }

    // The character and the bubble stay out of the taskbar; the tray icon is how to find, hide or quit the app.
    System.Windows.Forms.NotifyIcon CreateTrayIcon(CharacterWindow character, string dataRoot)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show assistant", null, (_, _) => character.ShowAgain());
        menu.Items.Add("Hide for 1 hour", null, (_, _) => character.HideFor(TimeSpan.FromHours(1)));
        menu.Items.Add("Open data folder", null, (_, _) => Shell.OpenFolder(dataRoot));
        menu.Items.Add("Weekly review now", null, (_, _) => reviewNow?.Invoke());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Shutdown());

        var icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Assistant",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.DoubleClick += (_, _) =>
        {
            character.ShowAgain();
            character.OpenChat();
        };
        return icon;
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLog.Error("Unhandled exception", e.Exception);
        MessageBox.Show(
            $"Something went wrong: {e.Exception.Message}\n\nDetails are in {DiagnosticLog.FolderPath}",
            "Assistant");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tracker?.Flush(DateTime.Now); // write the window in progress

        // Without this, a dead icon lingers in the tray until the mouse passes over it.
        if (tray is not null)
        {
            tray.Visible = false;
            tray.Dispose();
        }
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
