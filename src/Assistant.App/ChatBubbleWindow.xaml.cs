using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Assistant.Core;
using Assistant.Core.Diagnostics;
using Assistant.Core.Llm;
using Assistant.Core.Memory;
using Assistant.Core.Storage;

namespace Assistant.App;

/// <summary>
/// The chat, shown as a speech bubble next to the character. It hides (keeping the conversation)
/// when you click elsewhere, press Esc or click the character again, and it sets the character's mood.
/// </summary>
public partial class ChatBubbleWindow : Window
{
    // Opening these runs code, so the user confirms first (safety rule in the spec).
    static readonly string[] RunnableExtensions =
        [".exe", ".bat", ".cmd", ".com", ".msi", ".ps1", ".vbs", ".js", ".scr", ".lnk"];

    readonly AssistantService assistant;
    readonly DependencyService dependencies;
    readonly DataFolder data;
    readonly CharacterMood mood;
    readonly ObservableCollection<object> items = []; // ChatItem and ReviewItem
    Action<ReviewCandidate, string?>? answerReview;     // kept text, or null for forget
    bool busy;
    int dialogsOpen; // while a dialog is up, losing focus must not hide the bubble

    /// <summary>When the bubble last hid itself because focus moved elsewhere.</summary>
    public DateTime LastAutoHide { get; private set; } = DateTime.MinValue;

    public ChatBubbleWindow(AssistantService assistant, DependencyService dependencies, DataFolder data, CharacterMood mood)
    {
        InitializeComponent();
        this.assistant = assistant;
        this.dependencies = dependencies;
        this.data = data;
        this.mood = mood;

        Messages.ItemsSource = items;
        items.Add(new ChatItem { Who = "Assistant", Text = "Hi! Ask me anything, or to find a file, for example \"where's my CV?\"" });

        Deactivated += (_, _) =>
        {
            if (dialogsOpen > 0 || !IsVisible) return;
            LastAutoHide = DateTime.UtcNow;
            Hide();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Hide();
            e.Handled = true;
        };
        // A review still waiting keeps the character's "weekly review" badge on while the bubble is closed.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && mood.Current == Mood.Review) mood.Set(Mood.Idle);
            else if (!IsVisible && HasOpenReview) mood.Set(Mood.Review);
        };

        _ = RefreshStatusAsync();
    }

    /// <summary>Shows the bubble beside the character: to its left if there is room, otherwise to its right.</summary>
    public void ShowNear(Rect character)
    {
        double screenLeft = SystemParameters.VirtualScreenLeft;
        double screenTop = SystemParameters.VirtualScreenTop;
        double screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        double screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        double left = character.Left - Width + 10;
        if (left < screenLeft) left = character.Right - 10;
        double top = character.Bottom - Height;

        Left = Math.Max(screenLeft, Math.Min(left, screenRight - Width));
        Top = Math.Max(screenTop, Math.Min(top, screenBottom - Height));

        Show();
        Activate();
        Input.Focus();
        ScrollToBottom();
    }

    public bool HasOpenReview => items.OfType<ReviewItem>().Any(item => item.IsOpen);

    /// <summary>Adds the weekly review cards. Nothing is saved unless the user presses Keep.</summary>
    public void ShowReview(IReadOnlyList<ReviewCandidate> candidates, Action<ReviewCandidate, string?> answer)
    {
        if (candidates.Count == 0 || HasOpenReview) return;
        answerReview = answer;
        items.Add(new ChatItem
        {
            Who = "Assistant · weekly review",
            Text = "Here's what I noticed this week. Keep what's right (edit it first if you like). " +
                   $"Anything you don't keep within {ReviewSchedule.AnswerWithin.TotalDays} days is forgotten.",
        });
        foreach (ReviewCandidate candidate in candidates) items.Add(new ReviewItem(candidate));
        if (!IsVisible) mood.Set(Mood.Review);
        ScrollToBottom();
    }

    /// <summary>A message from the assistant that isn't a reply, for example from the tray menu.</summary>
    public void Say(string text)
    {
        items.Add(new ChatItem { Who = "Assistant", Text = text });
        ScrollToBottom();
    }

    /// <summary>The review went unanswered: close its cards. Nothing was saved.</summary>
    public void ExpireReview()
    {
        foreach (ReviewItem item in items.OfType<ReviewItem>().Where(item => item.IsOpen))
            item.Close("Not reviewed, so forgotten.");
        if (mood.Current == Mood.Review) mood.Set(Mood.Idle);
    }

    void OnKeepFact(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ReviewItem item } || !item.IsOpen) return;
        string text = item.Fact.Trim();
        if (text.Length == 0) return; // an emptied fact: Forget is the way to drop it

        try
        {
            answerReview?.Invoke(item.Candidate, text);
            item.Close("Kept in memory.");
            mood.Set(Mood.Happy, TimeSpan.FromSeconds(2));
        }
        catch (IOException ex)
        {
            DiagnosticLog.Error("Could not save a reviewed fact", ex);
            Ask($"Couldn't save it: {ex.Message}");
        }
    }

    void OnForgetFact(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ReviewItem item } || !item.IsOpen) return;
        answerReview?.Invoke(item.Candidate, null);
        item.Close("Forgotten.");
    }

    async Task RefreshStatusAsync()
    {
        StatusText.Text = "Checking...";
        IReadOnlyList<DependencyStatus> statuses = await dependencies.CheckAllAsync(CancellationToken.None);

        // Short line in the bubble: problems spelled out, the rest as names. Hover for every detail.
        StatusText.Text = string.Join("   ", statuses.Select(s =>
            s.Ready ? $"✓ {s.Name}" : s.Optional ? $"– {s.Name}" : $"✗ {s.Name}: {s.Detail}"));
        StatusText.ToolTip = string.Join("\n", statuses.Select(s =>
            $"{(s.Ready ? "✓" : s.Optional ? "–" : "✗")} {s.Name}: {s.Detail}"));
    }

    async void OnRecheck(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    void OnOpenDataFolder(object sender, RoutedEventArgs e) => Shell.OpenFolder(data.Root);

    void OnClose(object sender, RoutedEventArgs e) => Hide();

    async void OnSend(object sender, RoutedEventArgs e)
    {
        string text = Input.Text.Trim();
        if (text.Length == 0 || busy) return;
        Input.Clear();
        await SendAsync(text, "You");
    }

    /// <summary>A question said aloud: shown as "You (voice)" and answered like a typed one. Null if busy or it failed.</summary>
    public Task<AssistantReply?> AskSpokenAsync(string text) => busy ? Task.FromResult<AssistantReply?>(null) : SendAsync(text, "You (voice)");

    public bool IsBusy => busy;

    async Task<AssistantReply?> SendAsync(string text, string who)
    {
        mood.Touch();
        items.Add(new ChatItem { Who = who, Text = text });
        ScrollToBottom();
        SetBusy(true);
        mood.Set(Mood.Thinking);
        var timer = Stopwatch.StartNew();

        try
        {
            AssistantReply reply = await assistant.SendAsync(text, CancellationToken.None);

            // Always show which brain answered, so it's clear when a message went to the cloud.
            string from = reply.Source == "local" ? "Assistant" : $"Assistant · {reply.Source}";
            items.Add(new ChatItem { Who = from, Text = reply.Text, Files = reply.Files, Notes = reply.Notes });
            ShowReplyMood(reply);

            // Timings only: never log what the user asked.
            DiagnosticLog.Info($"Reply in {timer.ElapsedMilliseconds} ms from {reply.Source}, {reply.ToolCalls} tool call(s), {reply.Files.Count} file(s)");
            return reply;
        }
        catch (HttpRequestException ex)
        {
            DiagnosticLog.Error("Ollama unreachable", ex);
            AddNote("I can't reach Ollama. Make sure it's running, then press Recheck.");
        }
        catch (LlmException ex)
        {
            DiagnosticLog.Error("Ollama returned an error", ex);
            AddNote($"Ollama returned an error: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            DiagnosticLog.Error("Ollama timed out", ex);
            AddNote("Ollama took too long to answer. Try again in a moment.");
        }
        finally
        {
            SetBusy(false);
            ScrollToBottom();
            if (IsVisible) Input.Focus();
        }
        return null;
    }

    void ShowReplyMood(AssistantReply reply)
    {
        if (reply.Files.Count > 0)
            mood.Set(Mood.FoundIt, TimeSpan.FromSeconds(4));
        else if (reply.Notes.Count > 0)
            mood.Set(Mood.Happy, TimeSpan.FromSeconds(3)); // saved or forgot something
        else if (reply.ToolCalls > 0)
            mood.Set(Mood.Confused, TimeSpan.FromSeconds(4)); // searched, found nothing
        else if (reply.Source.Contains("cloud unavailable", StringComparison.Ordinal))
            mood.Set(Mood.Tired, TimeSpan.FromSeconds(6));
        else
            mood.Set(Mood.Idle);
    }

    void AddNote(string text)
    {
        items.Add(new ChatItem { Who = "Assistant", Text = text });
        mood.Set(Mood.Confused, TimeSpan.FromSeconds(4));
    }

    void OnOpenFile(object sender, RoutedEventArgs e)
    {
        // A card can also be a folder (a remembered place): open it in Explorer.
        if (sender is FrameworkElement { Tag: string folder } && Directory.Exists(folder))
        {
            Shell.OpenFolder(folder);
            mood.Set(Mood.Happy, TimeSpan.FromSeconds(2));
            return;
        }

        if (sender is not FrameworkElement { Tag: string path } || !File.Exists(path))
        {
            Ask("That file no longer exists.");
            return;
        }

        if (RunnableExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())
            && Ask($"{Path.GetFileName(path)} is a program or script. Run it?",
                          MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Shell.OpenFile(path);
            mood.Set(Mood.Happy, TimeSpan.FromSeconds(2));
        }
        catch (Win32Exception ex)
        {
            Ask($"Windows couldn't open it: {ex.Message}");
        }
    }

    void OnRevealFile(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) Shell.RevealFile(path);
    }

    MessageBoxResult Ask(string text, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        dialogsOpen++;
        try
        {
            return MessageBox.Show(this, text, "Assistant", buttons, icon);
        }
        finally
        {
            dialogsOpen--;
        }
    }

    void SetBusy(bool value)
    {
        busy = value;
        SendButton.IsEnabled = !value;
        BusyText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    // Wait for the new message to be laid out before scrolling to it.
    void ScrollToBottom() => Dispatcher.InvokeAsync(Scroller.ScrollToEnd, DispatcherPriority.Background);
}
