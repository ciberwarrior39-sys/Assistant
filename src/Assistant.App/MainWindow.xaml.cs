using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Assistant.Core;
using Assistant.Core.Diagnostics;
using Assistant.Core.Llm;
using Assistant.Core.Storage;

namespace Assistant.App;

public partial class MainWindow : Window
{
    // Opening these runs code, so the user confirms first (safety rule in the spec).
    static readonly string[] RunnableExtensions =
        [".exe", ".bat", ".cmd", ".com", ".msi", ".ps1", ".vbs", ".js", ".scr", ".lnk"];

    readonly AssistantService assistant;
    readonly DependencyService dependencies;
    readonly DataFolder data;
    readonly ObservableCollection<ChatItem> items = [];
    bool busy;

    public MainWindow(AssistantService assistant, DependencyService dependencies, DataFolder data)
    {
        InitializeComponent();
        this.assistant = assistant;
        this.dependencies = dependencies;
        this.data = data;

        Messages.ItemsSource = items;
        items.Add(new ChatItem { Who = "Assistant", Text = "Hi! Ask me to find a file, for example \"where's my CV?\"" });
        Loaded += async (_, _) => await RefreshStatusAsync();
    }

    async Task RefreshStatusAsync()
    {
        StatusText.Text = "Checking...";
        IReadOnlyList<DependencyStatus> statuses = await dependencies.CheckAllAsync(CancellationToken.None);
        StatusText.Text = string.Join("     ", statuses.Select(s => $"{(s.Ready ? "✓" : s.Optional ? "–" : "✗")} {s.Name}: {s.Detail}"));
        StatusText.ToolTip = StatusText.Text;
    }

    async void OnRecheck(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    void OnOpenDataFolder(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{data.Root}\"") { UseShellExecute = true });

    async void OnSend(object sender, RoutedEventArgs e)
    {
        string text = Input.Text.Trim();
        if (text.Length == 0 || busy) return;

        Input.Clear();
        items.Add(new ChatItem { Who = "You", Text = text });
        ScrollToBottom();
        SetBusy(true);
        var timer = Stopwatch.StartNew();

        try
        {
            AssistantReply reply = await assistant.SendAsync(text, CancellationToken.None);
            // Always show which brain answered, so it's clear when a message went to the cloud.
            string who = reply.Source == "local" ? "Assistant" : $"Assistant · {reply.Source}";
            items.Add(new ChatItem { Who = who, Text = reply.Text, Files = reply.Files });
            // Timings only: never log what the user asked.
            DiagnosticLog.Info($"Reply in {timer.ElapsedMilliseconds} ms from {reply.Source}, {reply.ToolCalls} tool call(s), {reply.Files.Count} file(s)");
        }
        catch (HttpRequestException ex)
        {
            DiagnosticLog.Error("Ollama unreachable", ex);
            AddAssistantNote("I can't reach Ollama. Make sure it's running, then press Recheck.");
        }
        catch (LlmException ex)
        {
            DiagnosticLog.Error("Ollama returned an error", ex);
            AddAssistantNote($"Ollama returned an error: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            DiagnosticLog.Error("Ollama timed out", ex);
            AddAssistantNote("Ollama took too long to answer. Try again in a moment.");
        }
        finally
        {
            SetBusy(false);
            ScrollToBottom();
            Input.Focus();
        }
    }

    void OnOpenFile(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || !File.Exists(path))
        {
            MessageBox.Show(this, "That file no longer exists.", "Assistant");
            return;
        }

        if (RunnableExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            MessageBoxResult answer = MessageBox.Show(this,
                $"{Path.GetFileName(path)} is a program or script. Run it?",
                "Assistant", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(this, $"Windows couldn't open it: {ex.Message}", "Assistant");
        }
    }

    void OnRevealFile(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    void AddAssistantNote(string text) => items.Add(new ChatItem { Who = "Assistant", Text = text });

    void SetBusy(bool value)
    {
        busy = value;
        SendButton.IsEnabled = !value;
        BusyText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    // Wait for the new message to be laid out before scrolling to it.
    void ScrollToBottom() => Dispatcher.InvokeAsync(Scroller.ScrollToEnd, DispatcherPriority.Background);
}
