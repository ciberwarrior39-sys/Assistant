using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Assistant.Core.Characters;
using Assistant.Core.Storage;
using Microsoft.Win32;

namespace Assistant.App;

/// <summary>What the Settings window needs from the rest of the app.</summary>
public sealed record SettingsHost(
    AppConfig Config,
    string ConfigPath,
    Action<AppConfig> ApplyLive,       // colors, size, bubble theme, features, voice: shown right away
    Action<Mood> PreviewMood,          // let the character show a mood for a moment
    Action<bool> PreviewBubble,        // show or hide the bubble next to the character, without focus
    Action<string> TestVoice,          // say a sample sentence with the current voice settings
    Action Restart,
    Action TryScreensaver,             // start the screensaver in 2 seconds
    Action DeleteAllData,              // remove Documents\Assistant and close the app
    CharacterPacks Characters,         // the user's own characters in Documents\Assistant\characters
    string Version);

/// <summary>
/// Settings, saved to config.json. Most changes show up immediately on the character and the bubble;
/// Cancel puts everything back. Voice on/off, the push-to-talk keys and cloud keys apply after a restart.
/// </summary>
public sealed class SettingsWindow : Window
{
    static readonly Dictionary<string, string> MoodNames = new()
    {
        ["Idle"] = "Normal",
        ["Thinking"] = "Thinking",
        ["FoundIt"] = "Found it",
        ["Confused"] = "Hmm? (nothing found)",
        ["Happy"] = "Happy",
        ["Tired"] = "Tired (cloud down)",
        ["Sleepy"] = "Sleepy",
        ["Review"] = "Weekly review",
        ["HandRaised"] = "Hand raised (!)",
        ["Listening"] = "Listening",
        ["Speaking"] = "Speaking",
        ["TimeUp"] = "Time's up",
    };

    readonly SettingsHost host;
    readonly AppConfig original;  // what to go back to on Cancel
    readonly AppConfig edited;    // the shared config, changed live
    readonly TextBlock restartNote = new()
    {
        Text = "Some changes apply after a restart.",
        Foreground = Brushes.DarkOrange,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
    readonly Dictionary<string, (Button Swatch, TextBox Hex)> colorRows = [];
    readonly ComboBox lookBox = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
    readonly StackPanel slots = new();
    readonly Button openFolder = new() { Content = "Open its folder", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock colorsHint = new() { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    bool saved;

    /// <summary>An entry in the character list: the config value and what the user sees.</summary>
    sealed record LookChoice(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    public SettingsWindow(SettingsHost host)
    {
        this.host = host;
        edited = host.Config;
        original = edited.Copy();

        Title = "Assistant settings";
        Width = 540;
        Height = 620;
        MinWidth = 460;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 13;

        var tabs = new TabControl { Margin = new Thickness(10, 10, 10, 0) };
        tabs.Items.Add(new TabItem { Header = "Character", Content = Scroll(CharacterTab()) });
        tabs.Items.Add(new TabItem { Header = "Chat bubble", Content = Scroll(BubbleTab()) });
        tabs.Items.Add(new TabItem { Header = "Voice", Content = Scroll(VoiceTab()) });
        tabs.Items.Add(new TabItem { Header = "Features", Content = Scroll(FeaturesTab()) });
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == tabs) host.PreviewBubble(tabs.SelectedIndex == 1);
        };

        var save = new Button { Content = "Save", Padding = new Thickness(16, 4, 16, 4), IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();

        var buttons = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(save, Dock.Right);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        buttons.Children.Add(restartNote);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;

        Closed += (_, _) =>
        {
            host.PreviewBubble(false);
            if (saved) return;
            edited.CopyFrom(original); // Cancel or ✕: back to how it was
            host.ApplyLive(edited);
        };
    }

    // ---- Character -------------------------------------------------------------------------------

    UIElement CharacterTab()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Heading("Character"));
        FillLooks(edited.Appearance.Character);
        lookBox.SelectionChanged += (_, _) =>
        {
            if (lookBox.SelectedItem is not LookChoice choice || choice.Key == edited.Appearance.Character) return;
            edited.Appearance.Character = choice.Key;
            ShowSlots();
            Changed();
        };
        openFolder.Click += (_, _) =>
        {
            if (edited.Appearance.CustomCharacter is { } name) Shell.OpenFolder(host.Characters.PackFolder(name));
        };
        var pickRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        pickRow.Children.Add(lookBox);
        pickRow.Children.Add(openFolder);
        panel.Children.Add(pickRow);

        // A new, empty character: a folder per mood, filled below or by dropping files in.
        var newName = new TextBox { Width = 160, Text = "My character", VerticalContentAlignment = VerticalAlignment.Center };
        var create = new Button { Content = "Create my own", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        create.Click += (_, _) =>
        {
            string name;
            try
            {
                name = host.Characters.Create(newName.Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"Couldn't create the character's folder: {ex.Message}", "Assistant");
                return;
            }
            edited.Appearance.Character = Appearance.CustomPrefix + name;
            FillLooks(edited.Appearance.Character);
            ShowSlots();
            Changed();
        };
        var createRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        createRow.Children.Add(newName);
        createRow.Children.Add(create);
        panel.Children.Add(Labeled("New character", createRow));

        panel.Children.Add(slots);
        ShowSlots();

        panel.Children.Add(Heading("Colors"));
        panel.Children.Add(colorsHint);

        foreach (string mood in Appearance.DefaultColors.Keys) panel.Children.Add(ColorRow(mood));

        var reset = new Button { Content = "Reset colors", Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        reset.Click += (_, _) =>
        {
            foreach ((string mood, string color) in Appearance.DefaultColors) SetColor(mood, color);
            Changed();
        };
        panel.Children.Add(reset);

        panel.Children.Add(Heading("Size and motion"));
        var size = new ComboBox { Width = 140, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { "Small", "Medium", "Large" } };
        size.SelectedIndex = Array.IndexOf(Appearance.Sizes, edited.Appearance.Size);
        size.SelectionChanged += (_, _) =>
        {
            edited.Appearance.Size = Appearance.Sizes[Math.Max(0, size.SelectedIndex)];
            Changed();
        };
        panel.Children.Add(Labeled("Size", size));
        panel.Children.Add(Check("Show labels under the character (\"thinking…\", \"found it!\")", edited.Appearance.ShowLabels,
            value => edited.Appearance.ShowLabels = value));
        panel.Children.Add(Check("Calm animations (smaller bounces, no wobble)", edited.Appearance.CalmAnimations,
            value => edited.Appearance.CalmAnimations = value));
        return panel;
    }

    // The built-in characters, then the user's own, with the current one selected.
    void FillLooks(string selected)
    {
        var choices = new List<LookChoice>
        {
            new("blob", "Blob (default)"),
            new("robot", "Robot"),
            new("cat", "Cat"),
        };
        choices.AddRange(host.Characters.Names().Select(name => new LookChoice(Appearance.CustomPrefix + name, $"{name} (yours)")));
        lookBox.ItemsSource = choices;
        lookBox.SelectedItem = choices.FirstOrDefault(choice => choice.Key == selected) ?? choices[0];
    }

    // For a custom character: one row per mood with a preview, Choose… and Clear.
    void ShowSlots()
    {
        slots.Children.Clear();
        string? name = edited.Appearance.CustomCharacter;
        openFolder.Visibility = name is null ? Visibility.Collapsed : Visibility.Visible;
        colorsHint.Text = name is null
            ? "One color per mood. Click a square to pick a color, or type #RRGGBB. Click a name to see it."
            : "Your character keeps its own colors. These are the glow it gets for moods without their own animation.";
        if (name is null) return;

        slots.Children.Add(Heading("Animations"));
        slots.Children.Add(Hint("For each mood, choose an animated GIF, or PNG frames (select them all; they play in name order). " +
                                "Only Normal is needed; the other moods use it with a colored glow until they have their own. " +
                                "Files are copied into the character's folder right away (Cancel doesn't undo that); " +
                                "replaced or cleared ones go to the Recycle Bin. Click a mood's name to see it."));
        foreach (string slot in CharacterPacks.Slots) slots.Children.Add(SlotRow(name, slot));
    }

    UIElement SlotRow(string name, string slot)
    {
        SlotContent content = host.Characters.Slot(name, slot);
        var preview = new Border
        {
            Width = 40,
            Height = 40,
            Margin = new Thickness(0, 0, 8, 0),
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Background = Brushes.WhiteSmoke,
            Child = new Image { Source = AnimationFiles.Thumbnail(content, 40), Stretch = Stretch.Uniform },
        };

        var title = new TextBlock { Width = 150, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "Show this mood on the character" };
        title.Inlines.Add(new Run(MoodNames.GetValueOrDefault(slot, slot)) { TextDecorations = TextDecorations.Underline });
        title.MouseLeftButtonUp += (_, _) => host.PreviewMood(Enum.Parse<Mood>(slot));

        bool required = slot == nameof(Mood.Idle);
        var status = new TextBlock
        {
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = content.Kind == SlotKind.Empty && required ? Brushes.Firebrick : Brushes.DimGray,
            Text = content.Kind switch
            {
                SlotKind.Gif => "GIF",
                SlotKind.Frames when content.Files.Count == 1 => "picture",
                SlotKind.Frames => $"{content.Files.Count} PNG frames",
                _ => required ? "empty (needed)" : "uses Normal",
            },
        };

        var choose = new Button { Content = "Choose…", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 0) };
        var clear = new Button { Content = "Clear", Padding = new Thickness(8, 1, 8, 1), IsEnabled = content.Kind != SlotKind.Empty };
        choose.Click += (_, _) => ChooseFiles(name, slot);
        clear.Click += (_, _) =>
        {
            if (!Recycle(host.Characters.SlotFiles(name, slot))) return;
            SlotChanged(slot);
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(preview);
        row.Children.Add(title);
        row.Children.Add(status);
        row.Children.Add(choose);
        row.Children.Add(clear);
        return row;
    }

    void ChooseFiles(string name, string slot)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"{MoodNames.GetValueOrDefault(slot, slot)}: an animated GIF, or PNG frames",
            Filter = "Animated GIF or PNG frames (*.gif;*.png)|*.gif;*.png",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        IReadOnlyList<string> files = dialog.FileNames;
        if (CharacterPacks.Check(files) is { } problem)
        {
            MessageBox.Show(this, problem, "Assistant");
            return;
        }

        // Files picked from the slot's own folder are already in place.
        string folder = Path.GetFullPath(host.Characters.SlotFolder(name, slot)) + Path.DirectorySeparatorChar;
        if (files.Any(file => Path.GetFullPath(file).StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Those files are already in this mood's folder.", "Assistant");
            return;
        }

        if (!Recycle(host.Characters.SlotFiles(name, slot))) return;
        try
        {
            host.Characters.Fill(name, slot, files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't copy the files: {ex.Message}", "Assistant");
        }
        SlotChanged(slot);
    }

    // The row is rebuilt and the character shows the mood with its new animation.
    void SlotChanged(string slot)
    {
        ShowSlots();
        Changed(slot);
    }

    // Replaced and cleared animation files go to the Recycle Bin, in case they were the only copy.
    bool Recycle(IReadOnlyList<string> files)
    {
        try
        {
            foreach (string file in files)
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            MessageBox.Show(this, $"Couldn't remove the old files: {ex.Message}", "Assistant");
            return false;
        }
    }

    UIElement ColorRow(string mood)
    {
        string hex = edited.Appearance.MoodColors[mood];
        var swatch = new Button { Width = 34, Height = 22, Background = Fill(hex), ToolTip = "Pick a color", Margin = new Thickness(0, 0, 8, 0) };
        var text = new TextBox { Width = 90, Text = hex, VerticalContentAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            Width = 170,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "Show this mood on the character",
        };
        name.Inlines.Add(new Run(MoodNames.GetValueOrDefault(mood, mood)) { TextDecorations = TextDecorations.Underline });
        colorRows[mood] = (swatch, text);

        swatch.Click += (_, _) =>
        {
            using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.ColorTranslator.FromHtml(edited.Appearance.MoodColors[mood]) };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            SetColor(mood, $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
            Changed(mood);
        };
        text.TextChanged += (_, _) =>
        {
            string typed = text.Text.Trim().ToUpperInvariant();
            if (!typed.StartsWith('#')) typed = "#" + typed;
            bool valid = Appearance.IsColor(typed);
            text.Foreground = valid ? SystemColors.ControlTextBrush : Brushes.Red;
            if (!valid || typed == edited.Appearance.MoodColors[mood]) return;
            edited.Appearance.MoodColors[mood] = typed;
            swatch.Background = Fill(typed);
            Changed(mood);
        };
        name.MouseLeftButtonUp += (_, _) => host.PreviewMood(Enum.Parse<Mood>(mood));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(name);
        row.Children.Add(swatch);
        row.Children.Add(text);
        return row;
    }

    void SetColor(string mood, string hex)
    {
        edited.Appearance.MoodColors[mood] = hex;
        if (!colorRows.TryGetValue(mood, out var row)) return;
        row.Swatch.Background = Fill(hex);
        row.Hex.Text = hex;
    }

    // ---- Chat bubble -----------------------------------------------------------------------------

    UIElement BubbleTab()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Hint("The chat bubble is shown next to the character while this tab is open."));

        panel.Children.Add(Heading("Theme"));
        var light = new RadioButton { Content = "Light", GroupName = "theme", IsChecked = edited.Appearance.BubbleTheme == "light", Margin = new Thickness(0, 2, 0, 2) };
        var dark = new RadioButton { Content = "Dark", GroupName = "theme", IsChecked = edited.Appearance.BubbleTheme == "dark", Margin = new Thickness(0, 2, 0, 2) };
        light.Checked += (_, _) => { edited.Appearance.BubbleTheme = "light"; Changed(); };
        dark.Checked += (_, _) => { edited.Appearance.BubbleTheme = "dark"; Changed(); };
        panel.Children.Add(light);
        panel.Children.Add(dark);

        panel.Children.Add(Heading("Text size"));
        var value = new TextBlock { Width = 30, VerticalAlignment = VerticalAlignment.Center, Text = edited.Appearance.BubbleTextSize.ToString() };
        var slider = new Slider { Minimum = 11, Maximum = 18, Width = 220, IsSnapToTickEnabled = true, TickFrequency = 1, Value = edited.Appearance.BubbleTextSize };
        slider.ValueChanged += (_, _) =>
        {
            edited.Appearance.BubbleTextSize = (int)slider.Value;
            value.Text = edited.Appearance.BubbleTextSize.ToString();
            Changed();
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(slider);
        row.Children.Add(value);
        panel.Children.Add(row);
        return panel;
    }

    // ---- Voice -----------------------------------------------------------------------------------

    UIElement VoiceTab()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Check("Voice on (push-to-talk and spoken replies)", edited.VoiceEnabled,
            value => edited.VoiceEnabled = value, needsRestart: true));

        panel.Children.Add(Heading("Voice"));
        var voices = new List<string> { "Windows default" };
        try
        {
            voices.AddRange(Assistant.Core.Voice.Speaker.InstalledVoiceNames());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No speech engine: the list stays at the default entry.
        }
        var voice = new ComboBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = voices };
        int current = edited.VoiceName.Length == 0 ? 0 : voices.FindIndex(name => name.Contains(edited.VoiceName, StringComparison.OrdinalIgnoreCase));
        voice.SelectedIndex = Math.Max(0, current);
        voice.SelectionChanged += (_, _) =>
        {
            edited.VoiceName = voice.SelectedIndex <= 0 ? "" : voices[voice.SelectedIndex];
            Changed();
        };
        panel.Children.Add(voice);

        panel.Children.Add(Heading("Speed"));
        var speed = new Slider { Minimum = -10, Maximum = 10, Width = 300, IsSnapToTickEnabled = true, TickFrequency = 1, Value = edited.VoiceRate, HorizontalAlignment = HorizontalAlignment.Left };
        speed.ValueChanged += (_, _) =>
        {
            edited.VoiceRate = (int)speed.Value;
            Changed();
        };
        panel.Children.Add(speed);

        var test = new Button { Content = "Test voice", Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        test.Click += (_, _) => host.TestVoice("Hi! This is how I sound.");
        panel.Children.Add(test);

        panel.Children.Add(Heading("Push-to-talk keys"));
        panel.Children.Add(Hint("Click the box, then press the keys together (at least one of Ctrl, Alt, Shift or Win)."));
        var keys = new TextBox { Width = 200, Text = edited.PushToTalkKey, IsReadOnly = true, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
        keys.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                    or Key.LWin or Key.RWin or Key.Tab or Key.Escape)
                return;

            var parts = new List<string>();
            ModifierKeys modifiers = Keyboard.Modifiers;
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            if (parts.Count == 0) return; // a bare key would fire while typing
            parts.Add(key.ToString());

            keys.Text = string.Join("+", parts);
            edited.PushToTalkKey = keys.Text;
            Changed(needsRestart: true);
        };
        panel.Children.Add(keys);
        return panel;
    }

    // ---- Features --------------------------------------------------------------------------------

    UIElement FeaturesTab()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Heading("Watching and helping"));
        panel.Children.Add(Check("Activity log (which apps and windows you use, kept 7 days, never sent anywhere)", edited.ActivityLogging,
            value => edited.ActivityLogging = value));
        panel.Children.Add(Check("Game mode (hide and free the GPU while a fullscreen game runs)", edited.GameModeEnabled,
            value => edited.GameModeEnabled = value));
        panel.Children.Add(Check("Hand-raise (a silent \"!\" when you seem stuck)", edited.HandRaiseEnabled,
            value => edited.HandRaiseEnabled = value));

        panel.Children.Add(Heading("Screensaver"));
        panel.Children.Add(Check("Bounce around the screen before Windows turns it off", edited.ScreensaverEnabled,
            value => edited.ScreensaverEnabled = value));
        TimeSpan? off = Assistant.Core.Activity.Power.ScreenOffAfter();
        panel.Children.Add(Hint(off is { } screenOff
            ? $"Windows turns your screen off after {screenOff.TotalMinutes:0} minutes without input (Windows Settings > System > Power & battery)."
            : $"Windows never turns your screen off, so it starts after {edited.ScreensaverFallbackMinutes} minutes without input."));
        var before = new TextBlock { Width = 70, VerticalAlignment = VerticalAlignment.Center, Text = $"{edited.ScreensaverMinutesBeforeScreenOff} min" };
        var lead = new Slider { Minimum = 1, Maximum = 10, Width = 200, IsSnapToTickEnabled = true, TickFrequency = 1, Value = edited.ScreensaverMinutesBeforeScreenOff };
        lead.ValueChanged += (_, _) =>
        {
            edited.ScreensaverMinutesBeforeScreenOff = (int)lead.Value;
            before.Text = $"{edited.ScreensaverMinutesBeforeScreenOff} min";
            Changed();
        };
        var leadRow = new StackPanel { Orientation = Orientation.Horizontal };
        leadRow.Children.Add(lead);
        leadRow.Children.Add(before);
        panel.Children.Add(Labeled("Start before", leadRow));
        var tryIt = new Button { Content = "Try it now", Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        tryIt.Click += (_, _) => host.TryScreensaver();
        panel.Children.Add(tryIt);
        panel.Children.Add(Hint("It starts 2 seconds after the click; keep the mouse still. Any input stops it."));

        panel.Children.Add(Heading("Smarter chat (optional, free)"));
        var hint = Hint("Plain conversation can use a free cloud model. Files, memory, activity and screenshots never go there. Groq is recommended. ");
        var link = new Hyperlink(new Run("Get a free Groq key")) { NavigateUri = new Uri("https://console.groq.com/keys") };
        link.RequestNavigate += (_, e) =>
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        };
        hint.Inlines.Add(link);
        panel.Children.Add(hint);
        panel.Children.Add(Labeled("Groq key", KeyBox(edited.GroqApiKey, value => edited.GroqApiKey = value)));
        panel.Children.Add(Labeled("OpenRouter key", KeyBox(edited.OpenRouterApiKey, value => edited.OpenRouterApiKey = value)));
        panel.Children.Add(Hint("Keys are saved encrypted for your Windows account, in config.json on this computer only."));

        panel.Children.Add(Heading("Updates"));
        panel.Children.Add(Check("Check for a new version once a day and install it on the next start", edited.AutoUpdate,
            value => edited.AutoUpdate = value));
        panel.Children.Add(Hint($"This is version {host.Version}."));

        panel.Children.Add(Heading("Your data"));
        panel.Children.Add(Hint("Uninstalling the app keeps Documents\\Assistant (memory, settings, logs) in case you come back. " +
                                "To remove it all now, use this button. The assistant closes afterwards and starts fresh next time."));
        var delete = new Button { Content = "Delete all my data…", Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left, Foreground = Brushes.DarkRed };
        delete.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Delete everything in Documents\\Assistant: memory, settings, activity and chat logs? This can't be undone.",
                    "Delete all my data", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            saved = true; // nothing to put back
            Close();
            host.DeleteAllData();
        };
        panel.Children.Add(delete);
        return panel;
    }

    PasswordBox KeyBox(string value, Action<string> set)
    {
        var box = new PasswordBox { Width = 300, Password = value };
        box.PasswordChanged += (_, _) =>
        {
            set(box.Password.Trim());
            Changed(needsRestart: true);
        };
        return box;
    }

    // ---- Saving and previewing -------------------------------------------------------------------

    void Changed(string? mood = null, bool needsRestart = false)
    {
        host.ApplyLive(edited);
        if (mood is not null) host.PreviewMood(Enum.Parse<Mood>(mood));
        if (needsRestart) restartNote.Visibility = Visibility.Visible;
    }

    void Changed(bool needsRestart) => Changed(null, needsRestart);

    void Save()
    {
        try
        {
            edited.Save(host.ConfigPath);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"Couldn't save config.json: {ex.Message}", "Assistant");
            return;
        }
        saved = true;

        bool restart = restartNote.Visibility == Visibility.Visible
            && MessageBox.Show(this, "Some changes apply after a restart. Restart the assistant now?", "Assistant",
                               MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        Close();
        if (restart) host.Restart();
    }

    // ---- Small builders --------------------------------------------------------------------------

    static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 14, 0, 4) };

    static TextBlock Hint(string text) => new() { Text = text, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };

    static UIElement Labeled(string label, UIElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }

    CheckBox Check(string text, bool value, Action<bool> set, bool needsRestart = false)
    {
        var box = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
        box.Click += (_, _) =>
        {
            set(box.IsChecked == true);
            Changed(needsRestart);
        };
        return box;
    }

    static SolidColorBrush Fill(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
