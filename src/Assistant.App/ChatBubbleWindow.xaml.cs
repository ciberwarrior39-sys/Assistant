using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    readonly ObservableCollection<object> items = []; // ChatItem, ReviewItem, CloudConfirmItem, TextHelpItem and TodoListItem
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
    public void ShowNear(Rect character) => ShowNear(character, activate: true);

    /// <summary>Shows the bubble without taking focus, so Settings can preview the theme next to it.</summary>
    public void Preview(Rect character)
    {
        ShowActivated = false;
        ShowNear(character, activate: false);
        ShowActivated = true;
    }

    void ShowNear(Rect character, bool activate)
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
        if (activate)
        {
            Activate();
            Input.Focus();
        }
        ScrollToBottom();
    }

    /// <summary>Light or dark theme and text size, applied right away (the Settings window's live preview).</summary>
    public void ApplyAppearance(Appearance settings)
    {
        bool dark = settings.BubbleTheme == "dark";
        SetBrush("Surface", dark ? "#1F1F23" : "#FFFFFF");
        SetBrush("Edge", dark ? "#3A3A42" : "#D0D0D0");
        SetBrush("Text", dark ? "#ECECEC" : "#1E1E1E");
        SetBrush("Muted", dark ? "#A0A0AA" : "#6B6B6B");
        SetBrush("CardEdge", dark ? "#3A3A42" : "#DDDDDD");
        SetBrush("ReviewSurface", dark ? "#2E2A1F" : "#FFF9EC");
        SetBrush("ReviewEdge", dark ? "#8A7333" : "#E8C77A");
        SetBrush("Note", dark ? "#5CC98A" : "#2E8B57");
        SetBrush("InputSurface", dark ? "#2A2A31" : "#FFFFFF");

        double size = settings.BubbleTextSize;
        FontSize = size;
        Resources["SmallSize"] = size - 2;
        Resources["LabelSize"] = size - 1;
        Resources["TitleSize"] = size + 1;
    }

    void SetBrush(string key, string hex) =>
        Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

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
            items.Add(new ChatItem
            {
                Who = from, Text = reply.Text, Files = reply.Files, Notes = reply.Notes, CloudQuestion = reply.CloudFollowUp,
                Choices = ChoiceButton.For(reply.Options), Question = text, Source = reply.Source,
            });
            if (reply.ShowTodos && assistant.Organizer is { } organizer) items.Add(new TodoListItem(organizer.Todos));
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

    /// <summary>
    /// "Solve with cloud chat" under a screen answer: the local model reads the text off the kept screenshot,
    /// and the user sees (and can fix) exactly what would be sent. Nothing leaves the computer yet.
    /// </summary>
    async void OnCloudFollowUp(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { Tag: ChatItem { CloudQuestion: { } question } chat } button) return;
        button.Visibility = Visibility.Collapsed;
        mood.Touch();

        // Selected text is already known: straight to the card that shows what would be sent.
        if (chat.CloudText is { } selected)
        {
            items.Add(new CloudConfirmItem(question, selected) { ForSelectedText = true });
            ScrollToBottom();
            return;
        }

        SetBusy(true, "Reading the text on your screen...");
        mood.Set(Mood.Thinking);
        try
        {
            string? text = await assistant.ReadScreenTextAsync(CancellationToken.None);
            if (text is null)
                AddNote("That screenshot is gone: I only keep it for 10 minutes. Ask me to look at the screen again.");
            else if (text.Length == 0)
                AddNote("I couldn't read any text in that window.");
            else
            {
                items.Add(new CloudConfirmItem(question, text));
                mood.Set(Mood.Idle);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or LlmException or TaskCanceledException)
        {
            DiagnosticLog.Error("Reading the screen text failed", ex);
            AddNote("I couldn't read the text off the screenshot. Make sure Ollama is running, then try again.");
            button.Visibility = Visibility.Visible;
        }
        finally
        {
            SetBusy(false);
            ScrollToBottom();
        }
    }

    async void OnSendToCloud(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not FrameworkElement { Tag: CloudConfirmItem item } || !item.IsOpen) return;
        string question = item.Question.Trim(), text = item.ScreenText.Trim();
        if (text.Length == 0) return; // an emptied text: Cancel is the way out
        if (question.Length == 0) question = "Solve this.";

        item.Close("Sent to the cloud chat.");
        SetBusy(true, "Asking the cloud chat...");
        mood.Set(Mood.Thinking);
        var timer = Stopwatch.StartNew();
        try
        {
            AssistantReply reply = item.ForSelectedText
                ? await assistant.AskCloudAboutTextAsync(question, text, CancellationToken.None)
                : await assistant.AskCloudAboutScreenAsync(question, text, CancellationToken.None);
            string from = reply.Source == "local" ? "Assistant" : $"Assistant · {reply.Source}";
            bool answered = !reply.Source.StartsWith("local", StringComparison.Ordinal);
            items.Add(new ChatItem { Who = from, Text = reply.Text, CopyText = item.ForSelectedText && answered ? reply.Text : null });
            ShowReplyMood(reply);
            DiagnosticLog.Info($"{(item.ForSelectedText ? "Text" : "Screen")} follow-up in {timer.ElapsedMilliseconds} ms from {reply.Source}");
        }
        catch (TaskCanceledException ex)
        {
            DiagnosticLog.Error("Cloud follow-up timed out", ex);
            AddNote("The cloud chat took too long to answer. Try again in a moment.");
        }
        finally
        {
            SetBusy(false);
            ScrollToBottom();
            if (IsVisible) Input.Focus();
        }
    }

    void OnCancelCloud(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CloudConfirmItem item } && item.IsOpen)
            item.Close("Not sent. Nothing left this computer.");
    }

    /// <summary>
    /// The character's menu > "Help with selected text": reads what the user marked (or copied) and shows it on a card
    /// with what can be done with it. Nothing is sent anywhere here.
    /// </summary>
    public async Task OfferTextHelpAsync(Func<Task<(PickedText? Text, string Problem)>> pick)
    {
        if (busy) return;
        mood.Touch();
        SetBusy(true, "Reading the marked text...");
        try
        {
            (PickedText? picked, string problem) = await pick();
            if (picked is null)
            {
                AddNote(problem);
                return;
            }
            foreach (TextHelpItem older in items.OfType<TextHelpItem>().Where(card => card.IsOpen))
                older.Close("Replaced by newer text.");
            items.Add(new TextHelpItem(picked.Text, picked.Source));
            mood.Set(Mood.Idle);
        }
        finally
        {
            SetBusy(false);
            ScrollToBottom();
        }
    }

    async void OnTextTask(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { Tag: TextHelpItem card, CommandParameter: string name } || !card.IsOpen
            || !Enum.TryParse(name, out TextTask task)) return;
        await RunTextTaskAsync(card, task);
    }

    // Enter in the "ask your own question" box asks it (instead of the bubble's own Send).
    async void OnTextQuestionKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not FrameworkElement { Tag: TextHelpItem card }) return;
        e.Handled = true;
        if (!busy && card.IsOpen) await RunTextTaskAsync(card, TextTask.Ask);
    }

    async Task RunTextTaskAsync(TextHelpItem card, TextTask task)
    {
        string? question = null;
        if (task == TextTask.Ask)
        {
            question = card.Question.Trim();
            if (question.Length == 0) return;
        }

        // Lite has no local model: the only way is the cloud, and only after the user sees what would be sent.
        if (!assistant.LocalModelEnabled)
        {
            if (assistant.CloudAvailable)
                items.Add(new CloudConfirmItem(TextHelp.Instruction(task, question), TextHelp.Cut(card.Text, TextHelp.MaxCloudChars))
                {
                    ForSelectedText = true,
                });
            else
                AddNote("Lite mode has no local model, and cloud chat isn't connected. Settings > Features can connect it.");
            ScrollToBottom();
            return;
        }

        mood.Touch();
        items.Add(new ChatItem { Who = "You", Text = question ?? TextHelp.Label(task) });
        card.Question = "";
        SetBusy(true, "Working on the text...");
        mood.Set(Mood.Thinking);
        var timer = Stopwatch.StartNew();
        try
        {
            AssistantReply reply = await assistant.HelpWithTextAsync(task, card.Text, question, CancellationToken.None);
            items.Add(new ChatItem
            {
                Who = "Assistant",
                Text = reply.Text,
                Notes = reply.Notes,
                CopyText = reply.Text,
                CloudQuestion = reply.CloudFollowUp,
                CloudText = reply.CloudText,
            });
            ShowReplyMood(reply);
            // Timings only: never log the text.
            DiagnosticLog.Info($"Text help ({task}) in {timer.ElapsedMilliseconds} ms");
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
        }
    }

    // A choice under an action reply: an app to open, a confirmation, or Cancel.
    async void OnChoice(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not FrameworkElement { Tag: ChoiceButton choice } || !choice.IsEnabled) return;
        choice.CloseGroup();
        if (choice.Intent is not { } intent)
        {
            items.Add(new ChatItem { Who = "Assistant", Text = "OK, cancelled." });
            ScrollToBottom();
            return;
        }

        SetBusy(true);
        try
        {
            AssistantReply reply = await assistant.RunActionAsync(intent, CancellationToken.None);
            items.Add(new ChatItem { Who = "Assistant", Text = reply.Text, Files = reply.Files, Choices = ChoiceButton.For(reply.Options) });
            ShowReplyMood(reply);
        }
        finally
        {
            SetBusy(false);
            ScrollToBottom();
        }
    }

    // 👍 or 👎 under a reply: saved once, on this computer, then both buttons go quiet.
    void OnRate(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatItem { Question: { } question } item, Parent: Panel row } button) return;
        bool good = (string)button.Content == "👍";
        new FeedbackLog(data.FeedbackFolder).Append(new ReplyRating(DateTime.Now, good ? "good" : "bad", question, item.Text, item.Source));
        foreach (Button rate in row.Children.OfType<Button>().Where(other => other.Content is "👍" or "👎"))
        {
            rate.IsEnabled = false;
            if (rate != button) rate.Visibility = Visibility.Collapsed;
        }
        button.ToolTip = good ? "Thanks!" : "Thanks, saved in the feedback folder.";
    }

    void OnCopyReply(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatItem { CopyText: { } text } } button) return;
        try
        {
            Clipboard.SetText(text);
            button.Content = "Copied ✓";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            button.Content = "Clipboard busy, try again";
        }
    }

    void ShowReplyMood(AssistantReply reply)
    {
        if (reply.Files.Count > 0)
            mood.Set(Mood.FoundIt, TimeSpan.FromSeconds(4));
        else if (reply.Notes.Count > 0)
            mood.Set(Mood.Happy, TimeSpan.FromSeconds(3)); // saved or forgot something
        else if (reply.NothingFound)
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

    void SetBusy(bool value, string text = "Thinking...")
    {
        busy = value;
        BusyText.Text = text;
        SendButton.IsEnabled = !value;
        BusyText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    // Wait for the new message to be laid out before scrolling to it.
    void ScrollToBottom() => Dispatcher.InvokeAsync(Scroller.ScrollToEnd, DispatcherPriority.Background);
}
