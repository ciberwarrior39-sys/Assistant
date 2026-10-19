using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Assistant.Core.Search;
using Assistant.Core.Setup;
using Assistant.Core.Storage;
using Assistant.Core.Voice;

namespace Assistant.App;

/// <summary>
/// The first-run wizard (setup plan, part 2): privacy in plain words, the local brain (hardware check,
/// Ollama and the model, downloaded with progress bars), optional file search and voice, an optional free
/// cloud key with Groq recommended, and done. Everything it changes lands in config.json; closing it early
/// keeps what was done and shows the wizard again next time. "Run setup again" in the tray reopens it.
/// </summary>
public sealed class SetupWizard : Window
{
    static readonly string[] Models = ["qwen3.5:0.8b", "qwen3.5:2b", "qwen3.5:4b"];

    readonly AppConfig config;
    readonly string configPath;
    readonly OllamaRuntime runtime;
    readonly string speechModels;
    readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan }; // multi-GB downloads
    readonly CancellationTokenSource cancel = new();
    readonly HardwarePlan plan = HardwareCheck.Detect();

    readonly ContentControl page = new();
    readonly TextBlock stepLabel = new() { Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center };
    readonly Button back = new() { Content = "Back", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
    readonly Button next = new() { Content = "Next", Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
    readonly List<Func<UIElement>> pages;
    int index;
    bool busy;
    bool brainReady;

    public SetupWizard(AppConfig config, string configPath, OllamaRuntime runtime, string speechModels)
    {
        this.config = config;
        this.configPath = configPath;
        this.runtime = runtime;
        this.speechModels = speechModels;

        Title = "Set up your assistant";
        Icon = Shell.WindowIcon;
        Width = 600;
        Height = 560;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 13;
        Topmost = false;

        pages = [Welcome, Brain, Extras, Cloud, Done];

        back.Click += (_, _) => Go(index - 1);
        next.Click += (_, _) =>
        {
            if (index == pages.Count - 1) Finish();
            else Go(index + 1);
        };

        var buttons = new DockPanel { Margin = new Thickness(16, 8, 16, 16) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(back);
        right.Children.Add(next);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(stepLabel);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = page, Padding = new Thickness(24, 20, 24, 8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        Closing += (_, e) =>
        {
            if (busy && MessageBox.Show(this, "A download is still running. Stop it and close setup?", "Assistant",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            cancel.Cancel();
            Save(); // keep whatever was set up so far
        };

        Go(0);
    }

    void Go(int target)
    {
        index = Math.Clamp(target, 0, pages.Count - 1);
        page.Content = pages[index]();
        stepLabel.Text = $"Step {index + 1} of {pages.Count}";
        back.IsEnabled = index > 0 && !busy;
        next.Content = index == pages.Count - 1 ? "Start the assistant" : "Next";
        next.IsEnabled = !busy && (index != 1 || brainReady);
    }

    // ---- 1. Welcome and privacy ------------------------------------------------------------------

    UIElement Welcome()
    {
        var panel = NewPage();
        panel.Children.Add(PageTitle("Hi! Let's get your assistant ready."));
        panel.Children.Add(Text("It takes a few minutes, mostly downloading. First, what it does and what stays private:"));
        panel.Children.Add(Bullet("Everything it learns is kept in Documents\\Assistant as plain files you can open, edit or delete."));
        panel.Children.Add(Bullet("It can note which apps and windows you use, kept for 7 days and never sent anywhere. " +
                                  "Password managers and private browsing windows are never noted (exclusions.json)."));
        panel.Children.Add(Bullet("Your files, memory, activity and screenshots never leave this computer. Plain chat goes to a free " +
                                  "cloud model only if you connect one on step 4, and everything sent is logged in the sent folder."));
        panel.Children.Add(Bullet("The microphone is used only while you hold the talk keys, and audio is never saved."));
        panel.Children.Add(Check("Note which apps I use (activity log). You can turn this off any time in Settings.",
            config.ActivityLogging, value => config.ActivityLogging = value));
        return panel;
    }

    // ---- 2. The local brain ----------------------------------------------------------------------

    UIElement Brain()
    {
        var panel = NewPage();
        panel.Children.Add(PageTitle("The assistant's brain"));
        panel.Children.Add(Text(plan.Summary));

        var model = new ComboBox { Width = 160, ItemsSource = Models, SelectedItem = Models.Contains(config.Model) && config.SetupDone ? config.Model : plan.Model };
        panel.Children.Add(Labeled("Model", model));

        var useExisting = new CheckBox
        {
            Content = new TextBlock { Text = "Use the Ollama already installed on this PC (no second copy to download)", TextWrapping = TextWrapping.Wrap },
            IsChecked = !config.OllamaEmbedded,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 6, 0, 0),
        };
        panel.Children.Add(useExisting);

        var status = Text("");
        var bar = new ProgressBar { Height = 14, Margin = new Thickness(0, 8, 0, 4), Visibility = Visibility.Collapsed };
        var setUp = new Button { Content = "Download and set up", Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(setUp);
        panel.Children.Add(bar);
        panel.Children.Add(status);

        // Is there already an Ollama with this model? Then the step is done before it starts.
        _ = CheckExistingAsync();
        async Task CheckExistingAsync()
        {
            bool installed = await OllamaRuntime.IsAnsweringAsync(http, OllamaRuntime.UsualUrl, cancel.Token);
            if (installed) useExisting.Visibility = Visibility.Visible;
            else useExisting.IsChecked = false;

            Uri url = useExisting.IsChecked == true ? OllamaRuntime.UsualUrl : OllamaRuntime.EmbeddedUrl;
            if (await OllamaRuntime.IsAnsweringAsync(http, url, cancel.Token)
                && OllamaRuntime.HasModel(await OllamaRuntime.ModelsAsync(http, url, cancel.Token), (string)model.SelectedItem))
            {
                Ready(url, (string)model.SelectedItem);
                status.Text = $"✓ Ready: {model.SelectedItem} is already downloaded.";
                setUp.Content = "Check again";
            }
        }

        setUp.Click += async (_, _) =>
        {
            string chosen = (string)model.SelectedItem;
            bool existing = useExisting.IsChecked == true;
            Uri url = existing ? OllamaRuntime.UsualUrl : OllamaRuntime.EmbeddedUrl;
            SetBusy(true);
            bar.Visibility = Visibility.Visible;
            var progress = new Progress<SetupProgress>(p => Show(p, bar, status));
            try
            {
                if (!existing)
                {
                    if (!OllamaRuntime.IsInstalled) await runtime.InstallAsync(plan.NeedsRocm, progress, cancel.Token);
                    status.Text = "Starting Ollama...";
                    await runtime.StartAsync(cancel.Token);
                }
                if (!OllamaRuntime.HasModel(await OllamaRuntime.ModelsAsync(http, url, cancel.Token), chosen))
                    await OllamaRuntime.PullAsync(http, url, chosen, progress, cancel.Token);

                Ready(url, chosen);
                bar.Value = bar.Maximum;
                status.Text = $"✓ Ready: {chosen} is set up.";
            }
            catch (OperationCanceledException)
            {
                status.Text = "Stopped.";
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                           or TimeoutException or InvalidDataException or UnauthorizedAccessException)
            {
                status.Text = $"Something went wrong: {ex.Message}\nCheck the internet connection and press the button again.";
            }
            finally
            {
                SetBusy(false);
            }
        };

        // A different model or Ollama: check again whether it's already there.
        void Changed()
        {
            brainReady = false;
            status.Text = "";
            setUp.Content = "Download and set up";
            next.IsEnabled = false;
            _ = CheckExistingAsync();
        }
        model.SelectionChanged += (_, _) => Changed();
        useExisting.Click += (_, _) => Changed();

        panel.Children.Add(Hint("The model is about 1–3.5 GB, and the app's own Ollama (when needed) about 1–2 GB more. " +
                                "The app's copies live in its own folder, so uninstalling removes them."));
        return panel;
    }

    void Ready(Uri url, string model)
    {
        brainReady = true;
        config.OllamaEmbedded = url == OllamaRuntime.EmbeddedUrl;
        config.OllamaUrl = url.ToString();
        config.Model = model;
        next.IsEnabled = !busy;
    }

    // ---- 3. File search and voice ----------------------------------------------------------------

    UIElement Extras()
    {
        var panel = NewPage();
        panel.Children.Add(PageTitle("Finding files and talking"));

        panel.Children.Add(Heading("File search"));
        bool everything = new EverythingSearch().IsAvailable(out _);
        if (everything)
        {
            panel.Children.Add(Text("✓ Everything is installed and running, so file search is instant."));
        }
        else
        {
            panel.Children.Add(Text("Everything (free, from voidtools) finds any file on your drives instantly. Without it, " +
                                    "Windows Search is used, which only knows your own folders (Desktop, Documents, Downloads...)."));
            var status = Text("");
            var bar = new ProgressBar { Height = 14, Margin = new Thickness(0, 6, 0, 4), Visibility = Visibility.Collapsed };
            var install = new Button { Content = "Install Everything (recommended)", Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Left };
            install.Click += async (_, _) =>
            {
                SetBusy(true);
                bar.Visibility = Visibility.Visible;
                try
                {
                    bool done = await EverythingSetup.InstallAsync(http, new Progress<SetupProgress>(p => Show(p, bar, status)), cancel.Token);
                    status.Text = done && new EverythingSearch().IsAvailable(out _)
                        ? "✓ Everything is installed and running."
                        : "Everything isn't running yet. You can install it later; Windows Search is used until then.";
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    status.Text = "The download didn't work. Windows Search will be used for now.";
                }
                finally
                {
                    bar.Visibility = Visibility.Collapsed;
                    SetBusy(false);
                }
            };
            panel.Children.Add(install);
            panel.Children.Add(bar);
            panel.Children.Add(status);
        }

        panel.Children.Add(Heading("Voice"));
        panel.Children.Add(Check($"Push-to-talk: hold {config.PushToTalkKey} and talk; replies to spoken questions are read aloud.",
            config.VoiceEnabled, value => config.VoiceEnabled = value));
        var speech = new SpeechToText(speechModels);
        if (speech.IsModelDownloaded)
        {
            panel.Children.Add(Text("✓ The speech model is downloaded."));
        }
        else
        {
            var voiceStatus = Text("The speech model (about 140 MB) downloads the first time you talk, or now:");
            var download = new Button { Content = "Download it now", Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Left };
            var bar = new ProgressBar { Height = 14, IsIndeterminate = true, Margin = new Thickness(0, 6, 0, 4), Visibility = Visibility.Collapsed };
            download.Click += async (_, _) =>
            {
                SetBusy(true);
                bar.Visibility = Visibility.Visible;
                try
                {
                    await speech.EnsureModelAsync(cancel.Token);
                    voiceStatus.Text = "✓ The speech model is downloaded.";
                    download.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    voiceStatus.Text = "The download didn't work. It will be tried again the first time you talk.";
                }
                finally
                {
                    bar.Visibility = Visibility.Collapsed;
                    SetBusy(false);
                }
            };
            panel.Children.Add(voiceStatus);
            panel.Children.Add(download);
            panel.Children.Add(bar);
        }
        return panel;
    }

    // ---- 4. Optional cloud chat ------------------------------------------------------------------

    UIElement Cloud()
    {
        var panel = NewPage();
        panel.Children.Add(PageTitle("Smarter chat (optional, free)"));
        panel.Children.Add(Text("The small local model is good at finding things, less so at conversation. A free cloud model can " +
                                "answer plain chat instead. Files, memory, activity and screenshots still never leave this computer. " +
                                "Skip this and everything stays local."));

        panel.Children.Add(Heading("Groq (recommended: the most free use)"));
        var steps = Text("1. Sign up at ");
        steps.Inlines.Add(Link("console.groq.com", "https://console.groq.com/keys"));
        steps.Inlines.Add(new Run(" (it asks for a phone number to verify).\n2. Open API Keys and press Create API Key.\n3. Copy the key and paste it here:"));
        panel.Children.Add(steps);
        panel.Children.Add(KeyRow(config.GroqApiKey, value => config.GroqApiKey = value, new Uri("https://api.groq.com/openai/v1/models")));

        panel.Children.Add(Heading("Or OpenRouter (no phone needed, about 50 free messages a day)"));
        var alt = Text("Sign up at ");
        alt.Inlines.Add(Link("openrouter.ai", "https://openrouter.ai/settings/keys"));
        alt.Inlines.Add(new Run(", create a key under Keys, and paste it here:"));
        panel.Children.Add(alt);
        panel.Children.Add(KeyRow(config.OpenRouterApiKey, value => config.OpenRouterApiKey = value, new Uri("https://openrouter.ai/api/v1/key")));

        panel.Children.Add(Hint("Keys are saved encrypted for your Windows account, in config.json on this computer only. " +
                                "You can add or change them later in Settings."));
        return panel;
    }

    UIElement KeyRow(string value, Action<string> set, Uri check)
    {
        var box = new PasswordBox { Width = 330, Password = value };
        var result = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var test = new Button { Content = "Check", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        box.PasswordChanged += (_, _) =>
        {
            set(box.Password.Trim());
            result.Text = "";
        };
        test.Click += async (_, _) =>
        {
            if (box.Password.Trim().Length == 0) return;
            result.Text = "Checking...";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, check);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", box.Password.Trim());
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using HttpResponseMessage response = await http.SendAsync(request, timeout.Token);
                result.Text = response.IsSuccessStatusCode ? "✓ Works" : $"✗ Not accepted ({(int)response.StatusCode})";
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                result.Text = "✗ No connection";
            }
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        row.Children.Add(box);
        row.Children.Add(test);
        row.Children.Add(result);
        return row;
    }

    // ---- 5. Done ---------------------------------------------------------------------------------

    UIElement Done()
    {
        var panel = NewPage();
        panel.Children.Add(PageTitle("All set!"));
        panel.Children.Add(Text(brainReady
            ? $"The assistant runs on {config.Model}" + (config.GroqApiKey.Length > 0 || config.OpenRouterApiKey.Length > 0 ? ", with free cloud chat connected." : ", fully on this computer.")
            : "The brain isn't set up yet, so chat won't work until it is. You can run setup again from the tray menu."));
        panel.Children.Add(Heading("Getting started"));
        panel.Children.Add(Bullet("Click the character to chat, for example \"where's my CV?\" or \"what was I working on today?\"."));
        if (config.VoiceEnabled) panel.Children.Add(Bullet($"Hold {config.PushToTalkKey} to talk to it."));
        panel.Children.Add(Bullet("Drag it anywhere. Right-click it for Settings: colors, size, voice and more."));
        panel.Children.Add(Bullet("Say \"remember that…\" to teach it something, and \"flip a coin\" or \"set a timer for 5 minutes\" for quick help."));
        panel.Children.Add(Bullet("The tray icon (near the clock) has Settings, Run setup again and Quit."));
        return panel;
    }

    void Finish()
    {
        config.SetupDone = true;
        Save();
        DialogResult = true; // closes the wizard
    }

    void Save()
    {
        try
        {
            config.Save(configPath);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"Couldn't save config.json: {ex.Message}", "Assistant");
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    void SetBusy(bool value)
    {
        busy = value;
        back.IsEnabled = index > 0 && !busy;
        next.IsEnabled = !busy && (index != 1 || brainReady);
    }

    static void Show(SetupProgress progress, ProgressBar bar, TextBlock status)
    {
        if (progress.Fraction is { } fraction)
        {
            bar.IsIndeterminate = false;
            bar.Maximum = 1;
            bar.Value = fraction;
            status.Text = $"{progress.Stage}: {Megabytes(progress.Done)} of {Megabytes(progress.Total)}";
        }
        else
        {
            bar.IsIndeterminate = true;
            status.Text = progress.Stage + "...";
        }
    }

    static string Megabytes(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (1 << 20)} MB";

    static StackPanel NewPage() => new();

    static TextBlock PageTitle(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };

    static TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) };

    static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };

    static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 12, 0, 0) };

    static TextBlock Bullet(string text) => new() { Text = "•  " + text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 2, 0, 4) };

    static UIElement Labeled(string label, UIElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = 70, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }

    static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new Thickness(0, 10, 0, 4) };
        box.Click += (_, _) => set(box.IsChecked == true);
        return box;
    }

    static Hyperlink Link(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url) };
        link.RequestNavigate += (_, e) =>
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        };
        return link;
    }
}
