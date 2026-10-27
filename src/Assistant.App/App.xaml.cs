using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Velopack;
using Assistant.Core;
using Assistant.Core.Activity;
using Assistant.Core.Characters;
using Assistant.Core.Diagnostics;
using Assistant.Core.Llm;
using Assistant.Core.Memory;
using Assistant.Core.Organizer;
using Assistant.Core.Search;
using Assistant.Core.Setup;
using Assistant.Core.Storage;
using Assistant.Core.Tools;
using Assistant.Core.Voice;

namespace Assistant.App;

public partial class App : Application
{
    /// <summary>
    /// Velopack runs first: during install, update and uninstall it calls the app with special arguments,
    /// handles them and exits before any window appears.
    /// </summary>
    [STAThread]
    static void Main()
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                StartWithWindows.Remove();
                CharacterFileType.Remove();
                RemoveAppFiles();
            })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>
    /// Uninstall: the app's own Ollama, the models and the logs in %LocalAppData%\Assistant go too.
    /// Documents\Assistant (memory, settings, logs of what you did) is the user's and stays; Settings can delete it.
    /// Runs without any window and must finish within Velopack's 30 seconds.
    /// </summary>
    static void RemoveAppFiles()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Assistant");
        foreach (Process process in Process.GetProcessesByName("ollama"))
        {
            try
            {
                if (process.MainModule?.FileName.StartsWith(folder, StringComparison.OrdinalIgnoreCase) == true)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Not ours, or already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
        try
        {
            Thread.Sleep(500); // let Windows release the killed process's files
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever is left is small; uninstalling must not fail because of it.
        }
    }

    Mutex? singleInstance;
    System.Windows.Forms.NotifyIcon? tray;
    ActivityTracker? tracker;
    Action? reviewNow; // set once the review is wired up, used by the tray menu
    VoiceController? voice;
    Action? openSettings;    // set once everything exists, used by the tray menu
    SettingsWindow? settingsWindow;
    Speaker? testSpeaker;    // for "Test voice" while voice is turned off
    OllamaRuntime? ollamaRuntime; // the app's own Ollama, when setup chose it
    AppConfig? appConfig;
    string configFile = "";
    Updater? updater;
    System.Windows.Forms.ToolStripItem? updateItem; // "Restart to update", shown once an update is downloaded
    Func<Task>? checkForUpdates;                     // tray > Check for updates

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Launching the app twice must not create a second assistant. A second start that came from
        // double-clicking a character file hands the file to the running one instead.
        string? importFile = ImportHandoff.FileFromArguments(e.Args);
        singleInstance = new Mutex(true, @"Local\Assistant.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            singleInstance.Dispose();
            singleInstance = null;
            if (importFile is not null && ImportHandoff.SendToRunning(importFile))
            {
                Shutdown();
                return;
            }
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
        appConfig = config;
        configFile = data.ConfigFile;

        // Close while I play Riot games: with the Riot Client, League or Valorant open, don't start at all.
        if (config.QuitForRiotGames && RiotGames.RunningProgram() is { } riotProgram)
        {
            DiagnosticLog.Info($"{riotProgram} is open; not starting (Close while I play Riot games)");
            if (!e.Args.Contains(StartWithWindows.AutostartArgument))
                MessageBox.Show("A Riot game or the Riot Client is open, so the assistant won't start " +
                                "(Settings > Features > Close while I play Riot games).\n\nStart it again after you close the Riot Client.",
                                "Assistant");
            Shutdown();
            return;
        }

        Retention.Purge(data, Retention.RawLogs);

        // First run (or "Run setup again"): the wizard sets up the brain, file search, voice and cloud keys.
        string speechModels = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Assistant", "models");
        var piper = new PiperInstall(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Assistant", "piper"));
        var runtimeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        ollamaRuntime = new OllamaRuntime(runtimeHttp);
        if (!config.SetupDone) new SetupWizard(config, data.ConfigFile, ollamaRuntime, speechModels, piper).ShowDialog();

        // Full, Balanced or Lite: how heavy the assistant may be on this PC (chosen in setup, changed in Settings).
        PerformanceProfile profile = PerformanceProfile.For(config.Performance);
        Motion.FramesPerSecond = profile.AnimationFps;
        DiagnosticLog.Info($"Performance profile: {profile.Label}");

        // The app's own Ollama starts in the background; the bubble's status line shows when it's ready.
        // Lite runs no local model, so there's nothing to start.
        if (config.OllamaEmbedded && profile.LocalModel) _ = StartEmbeddedOllamaAsync(ollamaRuntime);

        var exclusions = new Exclusions(data.ExclusionsFile);
        var activity = new ActivityTracker(new ActivityLog(data.ActivityFolder), exclusions)
        {
            Enabled = config.ActivityLogging,
        };
        tracker = activity;

        // The app's own Ollama or one the user installed, as chosen in setup.
        var ollama = new HttpClient
        {
            BaseAddress = new Uri(config.OllamaUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(2),
        };
        // Everything when it's installed and running, otherwise Windows' own search index.
        var fileSearch = new FallbackFileSearch(new EverythingSearch(), new WindowsSearch());

        // Optional cloud chat, tried in this order. A provider without a key is skipped.
        CloudEndpoint[] cloudEndpoints =
        [
            new("Groq", new Uri("https://api.groq.com/openai/v1/"), config.GroqApiKey, config.GroqModel, "models")
            {
                // Groq's production chat models, best for conversation first. Used when the configured
                // model is retired or not allowed for this key.
                FallbackModels = ["openai/gpt-oss-120b", "llama-3.3-70b-versatile", "openai/gpt-oss-20b", "llama-3.1-8b-instant"],
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
        // "set a timer for 5 minutes", "remind me tomorrow at 9 to …": kept in reminders.json across restarts.
        var reminders = new Reminders(new ReminderStore(data.RemindersFile));
        // Notes and the to-do list, in notes.md and todo.md.
        var organizer = new OrganizerAnswers(new NotesStore(data.NotesFile), new TodoStore(data.TodoFile), reminders);
        var windowCapture = new WindowCapture(exclusions, config.GameExecutables); // "look at this": the window the user was using
        var localModel = new OllamaProvider(ollama, config.Model, config.ContextSize) { KeepAlive = profile.ModelKeepAlive };
        AssistantService? service = null; // the memory tools check the message being answered
        Func<string> currentUserText = () => service?.CurrentUserText ?? "";
        var tools = new List<ITool>
        {
            new SearchFilesTool(fileSearch),
            new RecentActivityTool(activity, currentUserText),
            new RememberTool(memory, currentUserText),
            new RecallTool(memory),
            new ForgetTool(memory, currentUserText),
        };
        if (profile.ScreenReading) tools.Add(new LookAtScreenTool(localModel, windowCapture.Capture));
        var assistant = new AssistantService(localModel, tools, cloud, memory, reminders)
        {
            LocalModelEnabled = profile.LocalModel,
            Organizer = organizer,
            Conversations = new ConversationLog(data.ConversationsFolder),
            ShareAboutMeWithCloud = config.CloudKnowsAboutMe,
        };
        service = assistant;
        var resources = new ResourceMonitor(ollama);
        var dependencies = new DependencyService(ollama, config.Model, fileSearch, cloudEndpoints, cloudHttp, profile.LocalModel);

        var mood = new CharacterMood();
        var bubble = new ChatBubbleWindow(assistant, dependencies, data, mood);

        // Two minutes after the chat closes (not reopened meanwhile), the local model notes what it was about.
        var noteChat = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        noteChat.Tick += async (_, _) =>
        {
            noteChat.Stop();
            await assistant.SummarizeAsync(CancellationToken.None);
        };
        bubble.IsVisibleChanged += (_, _) =>
        {
            noteChat.Stop();
            if (!bubble.IsVisible) noteChat.Start();
        };
        var characters = new CharacterPacks(data.CharactersFolder);
        var character = new CharacterWindow(mood, bubble, characters, Path.Combine(data.Root, "placement.json"), data.Root, Shutdown);
        MainWindow = character;
        character.ApplyAppearance(config.Appearance);
        character.ApplyPerformance(profile);
        bubble.ApplyAppearance(config.Appearance);
        character.Show();
        CharacterLife? life = null; // made once game mode is known, below

        tray = CreateTrayIcon(character, data.Root);

        // Automatic updates (installed copies only): downloaded quietly, installed on the next start.
        var updates = new Updater();
        updater = updates;
        updater.Ready += version =>
        {
            if (updateItem is not null) updateItem.Visible = true;
            bubble.Say($"Version {version} is downloaded. It installs the next time the assistant starts, " +
                       "or right away from the tray menu: Restart to update.");
        };
        updater.Start(() => config.AutoUpdate);
        StartWithWindows.Sync(config.StartWithWindows);
        CharacterFileType.Register();
        checkForUpdates = async () =>
        {
            string result = await updates.CheckNowAsync();
            character.ShowAgain();
            bubble.Say(result);
        };

        // Push-to-talk. The speech model lives with the app's own files, not in the user's data folder.
        if (config.VoiceEnabled)
        {
            var hotkey = new PushToTalkHotkey(character, config.PushToTalkKey);
            if (!hotkey.IsRegistered)
            {
                DiagnosticLog.Info("Push-to-talk keys could not be registered");
                bubble.Say($"Push-to-talk is off: {config.PushToTalkKey} is already used by another app or isn't a valid key. " +
                           "Change pushToTalkKey in config.json and restart.");
            }
            voice = new VoiceController(hotkey, new SpeechToText(speechModels, profile.SpeechModel), new Speaker(config.VoiceName, config.VoiceRate, piper),
                                        bubble, character, mood);
        }

        // Screensaver: bounces around a few minutes before Windows turns the screen off.
        var screensaver = new Screensaver(character, mood, bubble, config);

        // Settings: one window at a time, changes shown live on the real character and bubble.
        openSettings = () =>
        {
            if (settingsWindow is { IsLoaded: true })
            {
                settingsWindow.Activate();
                return;
            }
            settingsWindow = new SettingsWindow(new SettingsHost(
                config,
                data.ConfigFile,
                ApplyLive: edited =>
                {
                    character.ApplyAppearance(edited.Appearance);
                    bubble.ApplyAppearance(edited.Appearance);
                    life?.SettingsChanged();
                    activity.Enabled = edited.ActivityLogging;
                    assistant.ShareAboutMeWithCloud = edited.CloudKnowsAboutMe;
                    StartWithWindows.Sync(edited.StartWithWindows);
                    voice?.ConfigureVoice(edited.VoiceName, edited.VoiceRate);
                    testSpeaker?.Configure(edited.VoiceName, edited.VoiceRate);
                },
                PreviewMood: shown =>
                {
                    character.ShowAgain();
                    mood.Set(shown, TimeSpan.FromSeconds(3));
                },
                PreviewBubble: show =>
                {
                    if (show) bubble.Preview(character.Bounds);
                    else if (!bubble.IsActive) bubble.Hide();
                },
                TestVoice: text =>
                {
                    if (voice is not null)
                    {
                        voice.Announce(text);
                        return;
                    }
                    testSpeaker ??= new Speaker(config.VoiceName, config.VoiceRate, piper);
                    testSpeaker.Speak(text);
                },
                Restart: Restart,
                TryScreensaver: screensaver.Try,
                DeleteAllData: () => DeleteAllData(data.Root),
                Characters: characters,
                CheckForUpdates: updates.CheckNowAsync,
                PendingUpdate: () => updates.PendingVersion,
                RestartToUpdate: RestartToUpdate,
                Installed: StartWithWindows.IsInstalled,
                Resources: () => resources.SampleAsync(voice?.SpeechModelLoaded == true, CancellationToken.None),
                Piper: piper,
                Version: updater?.CurrentVersion ?? "development build"));
            settingsWindow.Show();
        };
        character.SettingsRequested += () => openSettings?.Invoke();

        // Doing things on the PC: "open Spotify", "volume to 30", "take a screenshot", "empty the recycle bin" (asks first).
        // For a screenshot, the character and the bubble step out of the picture for a moment.
        bool bubbleWasOpen = false;
        assistant.Actions = new WindowsActions(
            searchEngine: () => config.SearchEngine,
            placeFacts: () => memory.Facts(MemoryKind.Places).Select(fact => fact.Text),
            gameMode: () => windowCapture.GameMode,
            openAssistantSettings: () => openSettings?.Invoke(),
            showAssistant: visible =>
            {
                if (!visible)
                {
                    bubbleWasOpen = bubble.IsVisible;
                    bubble.Hide();
                    character.Hide();
                }
                else
                {
                    character.ShowAgain();
                    if (bubbleWasOpen) character.OpenChat();
                }
            },
            ownWindow: () => new WindowInteropHelper(character).EnsureHandle());

        // Help with selected text: mark text anywhere, right-click the character. Read through Windows' accessibility
        // interface (or the last copied text), shown on a card; nothing is sent anywhere until the user picks a cloud option.
        character.TextHelpRequested += async () =>
        {
            character.ShowAgain();
            character.OpenChat();
            await bubble.OfferTextHelpAsync(() => SelectedText.PickAsync(windowCapture));
        };

        // A character file was double-clicked: preview it, then import it and switch to it.
        void ImportCharacter(string file)
        {
            character.ShowAgain();
            if (settingsWindow is { IsLoaded: true })
            {
                settingsWindow.Activate();
                settingsWindow.ImportFile(file); // Settings owns the config while it's open
                return;
            }
            if (ImportCharacterWindow.Run(null, characters, file) is not { } result) return;
            config.Appearance.Character = Appearance.CustomPrefix + result.Name;
            if (result.Colors is { } colors)
            {
                foreach ((string slot, string color) in colors) config.Appearance.MoodColors[slot] = color;
            }
            try
            {
                config.Save(data.ConfigFile);
            }
            catch (IOException ex)
            {
                DiagnosticLog.Error("Could not save config.json", ex);
            }
            character.ApplyAppearance(config.Appearance);
            mood.Set(Mood.Happy, TimeSpan.FromSeconds(2));
        }
        ImportHandoff.Listen(ImportCharacter);
        if (importFile is not null) _ = Dispatcher.InvokeAsync(() => ImportCharacter(importFile), DispatcherPriority.ApplicationIdle);

        // A timer is due: show it on the character and in the bubble without stealing focus, and say it aloud.
        // In game mode the character stays hidden, so it is only said.
        bool gaming = false;
        reminders.Due += text =>
        {
            bubble.Say(text);
            // Also a Windows notification, seen even when the bubble is closed or the character is hidden.
            tray?.ShowBalloonTip(10_000, "Assistant", text, System.Windows.Forms.ToolTipIcon.None);
            if (!gaming)
            {
                character.ShowAgain();
                mood.Set(Mood.TimeUp, TimeSpan.FromSeconds(15));
            }
            if (voice is not null) voice.Announce(text);
            else System.Media.SystemSounds.Exclamation.Play();
        };

        // Reminders that fell due while the app was closed (PC off, or closed for a Riot game): said once, at start.
        if (reminders.TakeMissed() is { } missedText)
        {
            bubble.Say(missedText);
            tray?.ShowBalloonTip(10_000, "Assistant", missedText, System.Windows.Forms.ToolTipIcon.None);
            mood.Set(Mood.TimeUp, TimeSpan.FromSeconds(10));
        }

        // The silent hand-raise, from the same snapshots as the activity log.
        var handRaiser = new HandRaiser(mood, bubble, character, new StruggleDetector(exclusions),
                                        new FeedbackLog(data.FeedbackFolder), config, data.ConfigFile);

        // Petting, idle tricks, downloads, the battery, breaks, coming back, hats and the birthday. Quiet in game mode.
        var characterLife = new CharacterLife(character, mood, config, data.ConfigFile, () => gaming,
                                              () => memory.Facts(MemoryKind.AboutMe).Select(fact => fact.Text), bubble.Say);
        life = characterLife;

        // Game mode: free the GPU, hide, and answer through the cloud while a game is in front.
        var gameMode = new GameModeDetector(config.GameExecutables);
        gameMode.Changed += async on =>
        {
            DiagnosticLog.Info(on ? "Game mode on" : "Game mode off");
            gaming = on;
            character.SetGameMode(on);
            if (!on) characterLife.WelcomeBack();
            windowCapture.GameMode = on;
            assistant.CloudOnly = on;
            handRaiser.Paused = on;
            if (on) screensaver.Stop();
            if (voice is not null) voice.Paused = on;
            if (on) handRaiser.LowerNow();
            if (on) await localModel.UnloadAsync(CancellationToken.None);
        };

        // One look at the foreground window per second feeds both the activity log and game mode.
        var watcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        watcher.Tick += (_, _) =>
        {
            ForegroundSnapshot snapshot = Desktop.Capture();
            activity.Observe(snapshot);
            windowCapture.Observe(snapshot);
            if (config.GameModeEnabled) gameMode.Observe(snapshot);
            handRaiser.Observe(snapshot);
            screensaver.Observe(snapshot, gaming);
            characterLife.Observe(snapshot);
        };
        watcher.Start();

        // Close while I play Riot games: every 2 seconds, a look at the names of running programs (nothing else).
        // Not while Settings is open, so ticking the box doesn't quit before it's saved.
        var riotWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        riotWatch.Tick += async (_, _) =>
        {
            if (!config.QuitForRiotGames || settingsWindow is { IsLoaded: true }) return;
            if (RiotGames.RunningProgram() is not { } riot) return;
            riotWatch.Stop();
            DiagnosticLog.Info($"{riot} opened; quitting (Close while I play Riot games)");
            // A separately installed Ollama keeps running, but without our model in memory.
            using var unload = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await localModel.UnloadAsync(unload.Token);
            Shutdown();
        };
        riotWatch.Start();

        // The model starts loading when the chat bubble opens, so it's ready by the time the question is typed;
        // between uses it leaves memory after the profile's keep-alive time.
        bubble.IsVisibleChanged += (_, _) =>
        {
            if (bubble.IsVisible && profile.LocalModel && !gaming) _ = localModel.WarmUpAsync();
        };

        // What each part uses, in the developer log every 10 minutes (and live in Settings > Features).
        var logResources = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        logResources.Tick += async (_, _) =>
            DiagnosticLog.Info(ResourceMonitor.Describe(await resources.SampleAsync(voice?.SpeechModelLoaded == true, CancellationToken.None)));
        logResources.Start();

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
        menu.Items.Add("Settings…", null, (_, _) => openSettings?.Invoke());
        menu.Items.Add("Hide for 1 hour", null, (_, _) => character.HideFor(TimeSpan.FromHours(1)));
        menu.Items.Add("Open data folder", null, (_, _) => Shell.OpenFolder(dataRoot));
        menu.Items.Add("Weekly review now", null, (_, _) => reviewNow?.Invoke());
        menu.Items.Add("Run setup again", null, (_, _) => RunSetupAgain());
        menu.Items.Add("Check for updates", null, async (_, _) =>
        {
            if (checkForUpdates is not null) await checkForUpdates();
        });
        updateItem = menu.Items.Add("Restart to update", null, (_, _) => RestartToUpdate());
        updateItem.Visible = false;
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Shutdown());

        var icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = Shell.TrayIcon(),
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

    static async Task StartEmbeddedOllamaAsync(OllamaRuntime runtime)
    {
        try
        {
            await runtime.StartAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Error("The app's Ollama didn't start", ex);
        }
    }

    /// <summary>Installs a downloaded update now: closes everything cleanly, then Velopack restarts the new version.</summary>
    void RestartToUpdate()
    {
        tracker?.Flush(DateTime.Now);
        tracker = null;
        voice?.Dispose();
        voice = null;
        ollamaRuntime?.Stop();
        if (tray is not null) tray.Visible = false;
        singleInstance?.ReleaseMutex();
        singleInstance?.Dispose();
        singleInstance = null;
        DiagnosticLog.Info("Restarting to install an update");
        updater?.RestartToUpdate();
    }

    /// <summary>
    /// Settings > "Delete all my data": removes Documents\Assistant (memory, logs, settings) and closes the app.
    /// The next start begins fresh, with the setup wizard.
    /// </summary>
    void DeleteAllData(string root)
    {
        if (tracker is not null) tracker.Enabled = false;
        tracker = null; // nothing may be written while the folder goes
        try
        {
            Directory.Delete(root, recursive: true);
            DiagnosticLog.Info("All user data deleted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Some user data couldn't be deleted", ex);
            MessageBox.Show($"Some files couldn't be deleted (one may be open in another program). You can delete what's left in {root} by hand.", "Assistant");
        }
        Shutdown();
    }

    // The wizard runs at the next start; restarting also picks up a new model or Ollama.
    void RunSetupAgain()
    {
        if (appConfig is null) return;
        appConfig.SetupDone = false;
        try
        {
            appConfig.Save(configFile);
        }
        catch (IOException ex)
        {
            DiagnosticLog.Error("Could not save config.json", ex);
            return;
        }
        Restart();
    }

    /// <summary>Starts a fresh copy of the app and closes this one (for settings that apply on restart).</summary>
    void Restart()
    {
        // Let go of the single-instance lock first, or the new copy would think one is already running.
        singleInstance?.ReleaseMutex();
        singleInstance?.Dispose();
        singleInstance = null;
        DiagnosticLog.Info("Restarting for new settings");
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tracker?.Flush(DateTime.Now); // write the window in progress
        voice?.Dispose();
        testSpeaker?.Dispose();
        ollamaRuntime?.Stop();

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
