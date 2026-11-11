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
using System.Windows.Media.Animation;
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
    readonly ObservableCollection<object> items = []; // WelcomeItem, ChatItem, ReviewItem, CloudConfirmItem, TextHelpItem and TodoListItem
    readonly Storyboard typing = new();  // the three dots while it works
    string lastSent = "";                // ↑ in an empty box brings it back
    readonly DispatcherTimer recheck = new() { Interval = TimeSpan.FromSeconds(10) }; // while something needed is missing
    readonly DateTime startedAt = DateTime.UtcNow;
    bool statusBroken;
    Action<ReviewCandidate, string?>? answerReview;     // kept text, or null for forget
    bool busy;
    int dialogsOpen; // while a dialog is up, losing focus must not hide the bubble

    /// <summary>When the bubble last hid itself because focus moved elsewhere.</summary>
    public DateTime LastAutoHide { get; private set; } = DateTime.MinValue;

    /// <summary>⋯ > Settings.</summary>
    public event Action? SettingsRequested;

    /// <summary>⋯ > What's new, or "See everything new" on the card after an update.</summary>
    public event Action? WhatsNewRequested;

    /// <summary>
    /// "Help with selected text" from the bubble: the suggestion (null), or a typed "translate this" (the task to run).
    /// The app picks up the marked text, like the character's menu.
    /// </summary>
    public event Action<TextHelpRequest?>? TextHelpRequested;

    public ChatBubbleWindow(AssistantService assistant, DependencyService dependencies, DataFolder data, CharacterMood mood)
    {
        InitializeComponent();
        this.assistant = assistant;
        this.dependencies = dependencies;
        this.data = data;
        this.mood = mood;

        Messages.ItemsSource = items;
        items.Add(new WelcomeItem());
        BuildTypingDots();

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

        // The app's own Ollama takes a while to start: while something needed is missing, look again every 10 seconds
        // (and whenever the bubble opens) until it works. The first minute counts as starting up, not as a problem.
        recheck.Tick += async (_, _) => await RefreshStatusAsync();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && statusBroken) await RefreshStatusAsync();
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

        // The tail's tip is at the window's edge, so the bubble sits right against the character.
        double left = character.Left - Width + 4;
        bool onLeft = left >= screenLeft;
        if (!onLeft) left = character.Right - 4;
        double top = character.Bottom - Height;

        Left = Math.Max(screenLeft, Math.Min(left, screenRight - Width));
        Top = Math.Max(screenTop, Math.Min(top, screenBottom - Height));
        PlaceTail(pointRight: onLeft, characterMiddle: character.Top + character.Height / 2 - Top);

        Show();
        if (activate)
        {
            Activate();
            Input.Focus();
        }
        ScrollToBottom();
    }

    /// <summary>
    /// The text size, applied right away (the Settings window's live preview). Light or dark and the character's color
    /// come from Theme, which the app applies to every window.
    /// </summary>
    public void ApplyAppearance(Appearance settings)
    {
        double size = settings.BubbleTextSize;
        FontSize = size;
        Resources["SmallSize"] = size - 1.5;
        Resources["TitleSize"] = size + 2;
    }

    // ---- The balloon's tail ------------------------------------------------------------------------------------------

    const double TailBaseHeight = 18;

    /// <summary>The tail on the side facing the character, level with its middle (kept clear of the rounded corners).</summary>
    void PlaceTail(bool pointRight, double characterMiddle)
    {
        double y = Math.Clamp(characterMiddle - TailBaseHeight / 2, Body.Margin.Top + 26, Height - Body.Margin.Bottom - 26 - TailBaseHeight);
        // The tail's base sits on the middle of the body's 2-pixel outline.
        Canvas.SetLeft(Tail, pointRight ? Width - Body.Margin.Right - 1 : Body.Margin.Left + 1);
        Canvas.SetTop(Tail, y);
        TailFlip.ScaleX = pointRight ? 1 : -1;
    }

    // ---- Header: status and the ⋯ menu ------------------------------------------------------------------------------

    void OnMore(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MenuEntry("New chat", StartNewChat));
        menu.Items.Add(MenuEntry("Check again", () => _ = RefreshStatusAsync()));
        menu.Items.Add(MenuEntry("Open data folder", () => Shell.OpenFolder(data.Root)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("What's new", () => WhatsNewRequested?.Invoke()));
        menu.Items.Add(MenuEntry("Settings…", () => SettingsRequested?.Invoke()));
        dialogsOpen++; // the menu takes focus: don't hide the bubble for that
        menu.Closed += (_, _) => dialogsOpen--;
        menu.IsOpen = true;
    }

    static MenuItem MenuEntry(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>A clean chat: the conversation so far is noted (kept 7 days) and the assistant starts fresh.</summary>
    void StartNewChat()
    {
        if (busy) return;
        assistant.StartNewChat();
        items.Clear();
        items.Add(new WelcomeItem());
        lastSent = "";
        Input.Focus();
    }

    // ---- Suggestions under the greeting -------------------------------------------------------------------------------

    async void OnSuggestion(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not FrameworkElement { Tag: Suggestion suggestion }) return;
        if (suggestion.Text is null)
        {
            TextHelpRequested?.Invoke(null);
            return;
        }
        if (suggestion.Send)
        {
            await SendAsync(suggestion.Text, "You");
            return;
        }
        Input.Text = suggestion.Text;
        Input.CaretIndex = Input.Text.Length;
        Input.Focus();
    }

    // ---- The input ----------------------------------------------------------------------------------------------------

    async void OnInputKey(object sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter is a new line.
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendTypedAsync();
            return;
        }
        // ↑ in an empty box: the last message, to fix and send again.
        if (e.Key == Key.Up && Input.Text.Length == 0 && lastSent.Length > 0)
        {
            e.Handled = true;
            Input.Text = lastSent;
            Input.CaretIndex = Input.Text.Length;
        }
    }

    void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = !busy && Input.Text.Trim().Length > 0;
    }

    void OnInputFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        InputBox.SetResourceReference(Border.BorderBrushProperty, Input.IsKeyboardFocused ? "Accent" : "Line");

    // ---- Working on it: three dots that take turns --------------------------------------------------------------------

    void BuildTypingDots()
    {
        System.Windows.Shapes.Ellipse[] dots = [Dot1, Dot2, Dot3];
        for (int i = 0; i < dots.Length; i++)
        {
            var pulse = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(420))
            {
                AutoReverse = true,
                BeginTime = TimeSpan.FromMilliseconds(i * 160),
            };
            Storyboard.SetTarget(pulse, dots[i]);
            Storyboard.SetTargetProperty(pulse, new PropertyPath(OpacityProperty));
            typing.Children.Add(pulse);
        }
        typing.Duration = TimeSpan.FromMilliseconds(1300);
        typing.RepeatBehavior = RepeatBehavior.Forever;
        Timeline.SetDesiredFrameRate(typing, 30);
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

    /// <summary>After an update: the highlights card, at the top of what's new in the chat (seen when it next opens).</summary>
    public void ShowUpdateCard(ReleaseNotes notes)
    {
        items.Add(new UpdateItem(notes));
        ScrollToBottom();
    }

    void OnWhatsNew(object sender, RoutedEventArgs e) => WhatsNewRequested?.Invoke();

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

    /// <summary>
    /// The dot by the name: green when everything needed works, amber when an optional part (cloud chat, Everything)
    /// is set up but failing, red when something needed is missing, with a line saying what. Hover for every part.
    /// </summary>
    async Task RefreshStatusAsync()
    {
        if (!statusBroken) StatusDot.ToolTip = "Checking…";
        IReadOnlyList<DependencyStatus> statuses = await dependencies.CheckAllAsync(CancellationToken.None);

        var broken = statuses.Where(s => !s.Ready && !s.Optional).ToList();
        bool optionalFailing = statuses.Any(s => !s.Ready && s.Optional && !s.Detail.StartsWith("not set up", StringComparison.Ordinal));
        bool startingUp = DateTime.UtcNow - startedAt < TimeSpan.FromMinutes(1);
        statusBroken = broken.Count > 0;
        if (statusBroken) recheck.Start();
        else recheck.Stop();

        string dot = statusBroken ? (startingUp ? "Muted" : "Bad") : optionalFailing ? "Warn" : "Good";
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dot);
        StatusDot.ToolTip = (statusBroken && startingUp ? "Starting up…\n" : "") + string.Join("\n", statuses.Select(s =>
            $"{(s.Ready ? "✓" : s.Optional ? "–" : "✗")} {s.Name}: {s.Detail}"));

        // While starting up, no alarm: the app's own Ollama is usually still loading.
        ProblemText.Text = statusBroken && !startingUp ? string.Join(" ", broken.Select(Advice)) + " I'll keep checking." : "";
        ProblemText.Visibility = ProblemText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // What's wrong in plain words, and what to do about it.
    static string Advice(DependencyStatus status) => status.Name switch
    {
        "Ollama" when status.Detail.StartsWith("model missing", StringComparison.Ordinal) =>
            "The local model isn't downloaded yet: tray > Run setup again gets it.",
        "Ollama" => "The local model isn't answering. Restarting the assistant usually fixes it.",
        _ => $"{status.Name}: {status.Detail}.",
    };

    void OnClose(object sender, RoutedEventArgs e) => Hide();

    async void OnSend(object sender, RoutedEventArgs e) => await SendTypedAsync();

    async Task SendTypedAsync()
    {
        string text = Input.Text.Trim();
        if (text.Length == 0 || busy) return;
        Input.Clear();
        lastSent = text;

        // "translate this", "summarise the selected text", "преведи това": about the marked text, not a chat question.
        if (TextHelpRequests.FromMessage(text) is { } request && TextHelpRequested is not null)
        {
            items.Add(new ChatItem { Who = "You", Text = text });
            ScrollToBottom();
            // "is this a scam? <the message>": the text came with the request, so there's nothing to pick up.
            if (request.Pasted is { } pasted)
                await OfferTextHelpAsync(() => Task.FromResult<(PickedText?, string)>((new PickedText(pasted, "pasted in the chat"), "")), request);
            else
                TextHelpRequested(request);
            return;
        }
        await SendAsync(text, "You");
    }

    /// <summary>
    /// A shortcut was pressed: its command runs as if typed. A quick result ("Opening Spotify.") shows as a note on the
    /// character without opening the chat; anything with more to it (an answer, files, a choice, the to-do list) opens it.
    /// </summary>
    public async Task RunShortcutAsync(string keys, string command, Action<string> showNote, Action openChat)
    {
        if (busy)
        {
            showNote("busy, try again in a moment");
            return;
        }
        AssistantReply? reply = await SendAsync(command, $"You ({keys})");
        if (reply is null)
        {
            openChat(); // the problem is in the chat
            return;
        }
        bool needsChat = IsVisible || reply.Files.Count > 0 || reply.Options.Count > 0 || reply.ShowTodos || reply.NothingFound
                         || reply.Source != "local" || reply.CloudFollowUp is not null || reply.Text.Length > 60 || reply.Text.Contains('\n');
        if (needsChat) openChat();
        else showNote(reply.Text);
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
                Choices = ChoiceButton.For(reply.Options), Question = text, Source = reply.Source, Sources = reply.Sources,
                WebSearchQuestion = reply.WebSearchQuestion, RetryQuestion = reply.RetryQuestion,
                LookUpQuestion = reply.LookUpQuestion,
            });
            if (reply.ShowTodos && assistant.Organizer is { } organizer) items.Add(new TodoListItem(organizer.Todos));
            ShowReplyMood(reply);
            if (statusBroken) _ = RefreshStatusAsync(); // a reply that worked may mean it's fixed

            // Timings only: never log what the user asked.
            DiagnosticLog.Info($"Reply in {timer.ElapsedMilliseconds} ms from {reply.Source}, {reply.ToolCalls} tool call(s), {reply.Files.Count} file(s)");
            return reply;
        }
        catch (HttpRequestException ex)
        {
            DiagnosticLog.Error("Ollama unreachable", ex);
            AddNote("I can't reach the local model (Ollama). If the assistant just started, give it a minute; otherwise restarting it usually fixes it.");
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

        SetBusy(true, "Reading the text on your screen…");
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
        SetBusy(true, "Asking the cloud chat…");
        mood.Set(Mood.Thinking);
        var timer = Stopwatch.StartNew();
        try
        {
            AssistantReply reply = item.ForSelectedText
                ? await assistant.AskCloudAboutTextAsync(question, text, CancellationToken.None)
                : await assistant.AskCloudAboutScreenAsync(question, text, CancellationToken.None);
            string from = reply.Source == "local" ? "Assistant" : $"Assistant · {reply.Source}";
            bool answered = !reply.Source.StartsWith("local", StringComparison.Ordinal);
            string answer = answered && item.AppendToAnswer is { } append ? reply.Text + "\n\n" + append : reply.Text;
            items.Add(new ChatItem { Who = from, Text = answer, CopyText = item.ForSelectedText && answered ? reply.Text : null });
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
    /// <param name="request">From a typed "translate this": the task to run on the text straight away (null: just the card).</param>
    public async Task OfferTextHelpAsync(Func<Task<(PickedText? Text, string Problem)>> pick, TextHelpRequest? request = null)
    {
        if (busy) return;
        mood.Touch();
        SetBusy(true, "Reading the marked text…");
        TextHelpItem? card = null;
        try
        {
            (PickedText? picked, string problem) = await pick();
            if (picked is null)
            {
                AddNote(problem);
                return;
            }
            foreach (TextHelpItem older in items.OfType<TextHelpItem>().Where(open => open.IsOpen))
                older.Close("Replaced by newer text.");
            card = new TextHelpItem(picked.Text, picked.Source);
            items.Add(card);
            mood.Set(Mood.Idle);
        }
        finally
        {
            SetBusy(false);
            ScrollToBottom();
        }

        if (card is null || request is null) return;

        // Typed "translate this": no language named means into English from Bulgarian, into Bulgarian otherwise.
        TextTask? task = request.TranslateEitherWay
            ? (card.Text.Any(c => c is >= 'А' and <= 'я') ? TextTask.ToEnglish : TextTask.ToBulgarian)
            : request.Task;
        if (task is { } run) await RunTextTaskAsync(card, run, showRequest: false);
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

    /// <param name="showRequest">False when the user's own typed words are already in the chat.</param>
    async Task RunTextTaskAsync(TextHelpItem card, TextTask task, bool showRequest = true)
    {
        if (task == TextTask.ScamCheck)
        {
            await CheckScamAsync(card, showRequest);
            return;
        }

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
                AddNote("Lite mode has no local model, and cloud chat isn't connected. Settings > Privacy and cloud can connect it.");
            ScrollToBottom();
            return;
        }

        mood.Touch();
        if (showRequest) items.Add(new ChatItem { Who = "You", Text = question ?? TextHelp.Label(task) });
        card.Question = "";
        SetBusy(true, "Working on the text…");
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
            AddNote("I can't reach the local model (Ollama). If the assistant just started, give it a minute; otherwise restarting it usually fixes it.");
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

    /// <summary>
    /// "Is this a scam?": first what the code found (instantly, on this computer), then a verdict. Cloud chat judges
    /// best, so with it connected the card shows exactly what would be sent and waits for Send; otherwise the local model
    /// weighs it. The same advice from code ends every verdict, whatever the model said.
    /// </summary>
    async Task CheckScamAsync(TextHelpItem card, bool showRequest)
    {
        mood.Touch();
        if (showRequest) items.Add(new ChatItem { Who = "You", Text = TextHelp.Label(TextTask.ScamCheck) });
        ScamReport report = ScamSigns.Check(card.Text);
        bool cloud = assistant.CloudAvailable, local = assistant.LocalModelEnabled;
        string found = report.Describe();
        if (cloud) found += "\n\nCloud chat can look at the whole message for a verdict. Check below what would be sent first.";
        else if (!local) found += "\n\n" + ScamSigns.Advice;
        items.Add(new ChatItem { Who = "Assistant", Text = found });
        DiagnosticLog.Info($"Scam check: {report.Signs.Count} signs, {report.LinkHosts.Count} links"); // never the text
        mood.Set(report.Signs.Count >= 2 ? Mood.Confused : Mood.Idle);

        string instruction = TextHelp.Instruction(TextTask.ScamCheck, report.ForModel());
        if (cloud)
        {
            items.Add(new CloudConfirmItem(instruction, TextHelp.Cut(card.Text, TextHelp.MaxCloudChars))
            {
                ForSelectedText = true,
                AppendToAnswer = ScamSigns.Advice,
            });
            ScrollToBottom();
            return;
        }
        if (!local)
        {
            ScrollToBottom();
            return;
        }

        SetBusy(true, "Weighing it up…");
        mood.Set(Mood.Thinking);
        try
        {
            AssistantReply reply = await assistant.HelpWithTextAsync(TextTask.ScamCheck, card.Text, report.ForModel(), CancellationToken.None);
            items.Add(new ChatItem { Who = "Assistant", Text = reply.Text + "\n\n" + ScamSigns.Advice, Notes = reply.Notes });
            ShowReplyMood(reply);
        }
        catch (Exception ex) when (ex is HttpRequestException or LlmException or TaskCanceledException)
        {
            DiagnosticLog.Error("Scam check with the local model failed", ex);
            AddNote("The local model didn't answer, so there's only what I found above. " + ScamSigns.Advice);
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

    // 👍 or 👎 under a reply: saved once, on this computer; the other button goes away and this one stays lit.
    void OnRate(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatItem { Question: { } question } item, CommandParameter: string rating, Parent: Panel row } button) return;
        new FeedbackLog(data.FeedbackFolder).Append(new ReplyRating(DateTime.Now, rating, question, item.Text, item.Source));
        foreach (Button rate in row.Children.OfType<Button>().Where(other => other.CommandParameter is "good" or "bad"))
        {
            rate.IsEnabled = false;
            if (rate != button) rate.Visibility = Visibility.Collapsed;
        }
        button.Opacity = 1;
        button.SetResourceReference(ForegroundProperty, "AccentInk");
        button.ToolTip = rating == "good" ? "Thanks!" : "Thanks, saved in the feedback folder.";
    }

    void OnCopyReply(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChatItem item } button) CopyWithTick(button, item.CopyText ?? item.Text, "Copy the answer");
    }

    void OnCopyCode(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MessagePart part } button) CopyWithTick(button, part.Text, "Copy the code");
    }

    // "Search the web" under a factual answer: Groq's model searches and answers again, with its sources.
    async void OnSearchWeb(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { Tag: ChatItem { WebSearchQuestion: { } question } } button) return;
        button.Visibility = Visibility.Collapsed;
        mood.Touch();
        SetBusy(true, "Searching the web… this takes a little while");
        mood.Set(Mood.Thinking);
        try
        {
            AssistantReply reply = await assistant.SearchWebAsync(question, CancellationToken.None);
            string from = reply.Source == "local" ? "Assistant" : $"Assistant · {reply.Source}";
            items.Add(new ChatItem { Who = from, Text = reply.Text, Sources = reply.Sources, Question = question, Source = reply.Source });
            ShowReplyMood(reply);
        }
        catch (Exception ex) when (ex is HttpRequestException or LlmException or TaskCanceledException)
        {
            DiagnosticLog.Error("Web search failed", ex);
            AddNote("The web search didn't work just now. Try again in a minute.");
            button.Visibility = Visibility.Visible;
        }
        finally
        {
            SetBusy(false);
            ScrollToBottom();
        }
    }

    // "Look it up" under an answer from saved facts: cloud chat (with Wikipedia) answers the same question.
    async void OnLookUp(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { Tag: ChatItem { LookUpQuestion: { } question } } button) return;
        button.Visibility = Visibility.Collapsed;
        mood.Touch();
        SetBusy(true, "Looking it up…");
        mood.Set(Mood.Thinking);
        try
        {
            AssistantReply reply = await assistant.LookUpAsync(question, CancellationToken.None);
            string from = reply.Source == "local" ? "Assistant" : $"Assistant · {reply.Source}";
            items.Add(new ChatItem
            {
                Who = from, Text = reply.Text, Sources = reply.Sources, Question = question, Source = reply.Source,
                WebSearchQuestion = reply.WebSearchQuestion,
            });
            ShowReplyMood(reply);
        }
        catch (Exception ex) when (ex is HttpRequestException or LlmException or TaskCanceledException)
        {
            DiagnosticLog.Error("Looking it up failed", ex);
            AddNote("Looking it up didn't work just now. Try again in a minute.");
            button.Visibility = Visibility.Visible;
        }
        finally
        {
            SetBusy(false);
            ScrollToBottom();
        }
    }

    // "Try again" after cloud chat didn't answer a factual question.
    async void OnRetry(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { Tag: ChatItem { RetryQuestion: { } question } } button) return;
        button.Visibility = Visibility.Collapsed;
        await SendAsync(question, "You");
    }

    // A source under an answer: opens in the browser (web addresses only).
    void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string url }
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? address)
            || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            DiagnosticLog.Error("Couldn't open a source link", ex);
        }
    }

    // Copies, and shows a tick on the button for a moment.
    void CopyWithTick(Button button, string text, string tip)
    {
        try
        {
            Clipboard.SetText(text);
            button.Content = "\uE73E"; // ✓ for a moment
            button.ToolTip = "Copied";
            var back = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            back.Tick += (_, _) =>
            {
                back.Stop();
                button.Content = "\uE8C8";
                button.ToolTip = tip;
            };
            back.Start();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            button.ToolTip = "The clipboard is busy. Try again.";
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

    // The character shows "thinking" already, so plain thinking is just the dots; longer jobs say what they're doing.
    void SetBusy(bool value, string text = "")
    {
        busy = value;
        BusyText.Text = text;
        BusyRow.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = !value && Input.Text.Trim().Length > 0;
        if (value && SystemParameters.ClientAreaAnimation) typing.Begin(this, isControllable: true);
        else typing.Stop(this);
    }

    // Wait for the new message to be laid out before scrolling to it.
    void ScrollToBottom() => Dispatcher.InvokeAsync(Scroller.ScrollToEnd, DispatcherPriority.Background);
}
