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
    Func<Task<string>> CheckForUpdates, // check GitHub Releases now; returns what happened
    Func<string?> PendingUpdate,       // the downloaded version waiting for a restart, or null
    Action RestartToUpdate,            // install the downloaded version now
    bool Installed,                    // installed with the setup file (not run from source)
    Func<Task<IReadOnlyList<Assistant.Core.Diagnostics.ResourceUsage>>> Resources, // what each part uses now
    Assistant.Core.Voice.PiperInstall Piper, // the natural voices, downloaded on request
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
        [CharacterPacks.NoticedSlot] = "Cursor nearby",
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
    readonly Button exportCharacter = new() { Content = "Export…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Save it as one file to share" };
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
        Icon = Shell.WindowIcon;
        Width = 780;
        Height = 640;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 13;
        FontFamily = Theme.UiFont;
        SetResourceReference(BackgroundProperty, "Paper");
        SetResourceReference(ForegroundProperty, "Ink");
        restartNote.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        colorsHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        // A list of pages on the left. The chat bubble shows next to the character while its page is open.
        var tabs = new TabControl { Style = (Style)FindResource("SideNav") };
        AddPage(tabs, "Character", CharacterTab());
        AddPage(tabs, "Chat bubble", BubbleTab());
        AddPage(tabs, "Voice", VoiceTab());
        AddPage(tabs, "Features", FeaturesSection());
        AddPage(tabs, "Performance", PerformanceSection());
        AddPage(tabs, "Privacy and cloud", Stack(CloudSection(), DataSection()));
        AddPage(tabs, "Startup and updates", Stack(StartupSection(), UpdatesSection()));
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == tabs) host.PreviewBubble(tabs.SelectedIndex == 1);
        };

        var save = new Button { Content = "Save", Style = (Style)FindResource("Primary"), Padding = new Thickness(18, 4, 18, 4), IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();

        var buttons = new DockPanel { Margin = new Thickness(16, 10, 16, 12) };
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(save, Dock.Right);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        buttons.Children.Add(restartNote);

        var root = new DockPanel();
        var bar = new Border { Child = buttons, BorderThickness = new Thickness(0, 1, 0, 0) };
        bar.SetResourceReference(Border.BorderBrushProperty, "Line");
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
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
        var panel = new StackPanel();
        panel.Children.Add(Heading("Which character"));
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
        exportCharacter.Click += (_, _) =>
        {
            if (edited.Appearance.CustomCharacter is { } name)
                new ExportCharacterWindow(host.Characters, name, edited.Appearance.MoodColors) { Owner = this }.ShowDialog();
        };
        var pickRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        pickRow.Children.Add(lookBox);
        pickRow.Children.Add(openFolder);
        pickRow.Children.Add(exportCharacter);
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

        // A character someone shared: one .assistant-character file (a zip of its folder works too).
        var import = new Button { Content = "Import a character file…", Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(110, 2, 0, 4) };
        import.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import a character",
                Filter = $"Assistant character (*{CharacterSharing.Extension})|*{CharacterSharing.Extension}|Zip file (*.zip)|*.zip",
            };
            if (dialog.ShowDialog(this) == true) ImportFile(dialog.FileName);
        };
        panel.Children.Add(import);

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
        panel.Children.Add(Labeled("Size", SizeControl()));
        panel.Children.Add(Check("Show labels under the character (\"thinking…\", \"found it!\")", edited.Appearance.ShowLabels,
            value => edited.Appearance.ShowLabels = value));
        panel.Children.Add(Check("Calm animations (smaller bounces, no wobble)", edited.Appearance.CalmAnimations,
            value => edited.Appearance.CalmAnimations = value));

        panel.Children.Add(Heading("Life"));
        panel.Children.Add(Hint("Small things that make it feel alive. None of them opens the chat, and all of them stop during a game."));
        panel.Children.Add(Check("Petting and poking (move the mouse back and forth over it, or double-click it)", edited.Appearance.Reactions,
            value => edited.Appearance.Reactions = value));
        panel.Children.Add(Check("React to downloads, the battery and you coming back", edited.Appearance.EventReactions,
            value => edited.Appearance.EventReactions = value));
        panel.Children.Add(Check("Little idle animations (blinking, looking around, stretching)", edited.Appearance.IdleTricks,
            value => edited.Appearance.IdleTricks = value));
        panel.Children.Add(Check("Seasonal hats (Halloween, Christmas, New Year, and your birthday if it remembers it)", edited.Appearance.SeasonalLooks,
            value => edited.Appearance.SeasonalLooks = value));
        panel.Children.Add(Check("Walk around the screen now and then (can be distracting)", edited.Appearance.Wander,
            value => edited.Appearance.Wander = value));

        int[] breaks = [0, 30, 45, 60, 90, 120];
        var breakBox = new ComboBox
        {
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = new[] { "Off", "30 minutes", "45 minutes", "1 hour", "1.5 hours", "2 hours" },
        };
        breakBox.SelectedIndex = Math.Max(0, Array.IndexOf(breaks, edited.BreakReminderMinutes));
        breakBox.SelectionChanged += (_, _) =>
        {
            edited.BreakReminderMinutes = breaks[Math.Max(0, breakBox.SelectedIndex)];
            Changed();
        };
        panel.Children.Add(Labeled("Break reminder", breakBox));
        panel.Children.Add(Hint("After this long at the PC it stretches and suggests a break. A 5-minute pause starts the count over; games don't count."));
        return panel;
    }

    // The character's size: drag the slider (steps of 5%), or type any whole percentage from 40 to 300.
    UIElement SizeControl()
    {
        var slider = new Slider
        {
            Minimum = Appearance.MinSizePercent,
            Maximum = Appearance.MaxSizePercent,
            Width = 220,
            SmallChange = 1,
            LargeChange = 10,
            Value = edited.Appearance.SizePercent,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Drag, or click the box and type an exact size",
        };
        var box = new TextBox { Width = 52, Text = edited.Appearance.SizePercent.ToString(), Margin = new Thickness(10, 0, 0, 0),
                                TextAlignment = TextAlignment.Right };
        var percent = new TextBlock { Text = "%", Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var reset = new Button { Content = "100%", Style = (Style)FindResource("Ghost"), Margin = new Thickness(6, 0, 0, 0),
                                 ToolTip = "Back to the normal size" };
        bool updating = false;

        void Apply(int value, bool fromBox)
        {
            value = Math.Clamp(value, Appearance.MinSizePercent, Appearance.MaxSizePercent);
            updating = true;
            if (!fromBox) box.Text = value.ToString();
            // The slider snaps to 5% steps when dragged, but shows a typed value as it is.
            slider.Value = value;
            updating = false;
            if (value == edited.Appearance.SizePercent) return;
            edited.Appearance.SizePercent = value;
            Changed();
        }

        slider.ValueChanged += (_, _) =>
        {
            if (updating) return;
            Apply((int)(Math.Round(slider.Value / 5) * 5), fromBox: false);
        };
        // Typed: applied as soon as it's a number in range; anything else is put right when the box is left.
        box.TextChanged += (_, _) =>
        {
            if (updating) return;
            if (int.TryParse(box.Text.Trim().TrimEnd('%'), out int typed)
                && typed is >= Appearance.MinSizePercent and <= Appearance.MaxSizePercent)
            {
                box.SetResourceReference(ForegroundProperty, "Ink");
                Apply(typed, fromBox: true);
            }
            else box.SetResourceReference(ForegroundProperty, "Bad");
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            updating = true;
            box.Text = edited.Appearance.SizePercent.ToString();
            updating = false;
            box.SetResourceReference(ForegroundProperty, "Ink");
        };
        // Enter just confirms the size (it must not press Save and close Settings).
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Keyboard.ClearFocus();
        };
        reset.Click += (_, _) => Apply(100, fromBox: false);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(slider);
        row.Children.Add(box);
        row.Children.Add(percent);
        row.Children.Add(reset);
        return row;
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

    /// <summary>
    /// Shows a character file's preview and imports it: it becomes the chosen character (saved with Save, like
    /// any change here). Also used when a character file is double-clicked while Settings is open.
    /// </summary>
    public void ImportFile(string file)
    {
        if (ImportCharacterWindow.Run(this, host.Characters, file) is not { } result) return;
        edited.Appearance.Character = Appearance.CustomPrefix + result.Name;
        if (result.Colors is { } colors)
        {
            foreach ((string mood, string color) in colors) SetColor(mood, color);
        }
        FillLooks(edited.Appearance.Character);
        ShowSlots();
        Changed();
        Activate();
    }

    // For a custom character: one row per mood with a preview, Choose… and Clear.
    void ShowSlots()
    {
        slots.Children.Clear();
        string? name = edited.Appearance.CustomCharacter;
        openFolder.Visibility = name is null ? Visibility.Collapsed : Visibility.Visible;
        exportCharacter.Visibility = openFolder.Visibility;
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
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new Image { Source = AnimationFiles.Thumbnail(content, 40), Stretch = Stretch.Uniform },
        };
        preview.SetResourceReference(Border.BorderBrushProperty, "Line");
        preview.SetResourceReference(Border.BackgroundProperty, "Field");

        var title = new TextBlock { Width = 150, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "Show this mood on the character" };
        title.Inlines.Add(new Run(MoodNames.GetValueOrDefault(slot, slot)) { TextDecorations = TextDecorations.Underline });
        if (Enum.TryParse(slot, out Mood shown)) title.MouseLeftButtonUp += (_, _) => host.PreviewMood(shown);
        else title.ToolTip = "Plays instead of Normal while the mouse pointer is near the character. Move the mouse close to see it.";

        bool required = slot == nameof(Mood.Idle);
        var status = new TextBlock
        {
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
            Text = content.Kind switch
            {
                SlotKind.Gif => "GIF",
                SlotKind.Frames when content.Files.Count == 1 => "picture",
                SlotKind.Frames => $"{content.Files.Count} PNG frames",
                _ => required ? "empty (needed)" : "uses Normal",
            },
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, content.Kind == SlotKind.Empty && required ? "Bad" : "Muted");

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
        var swatch = new Button { Width = 34, Height = 24, Background = Fill(hex), ToolTip = "Pick a color", Margin = new Thickness(0, 0, 8, 0),
                                  Style = (Style)FindResource("Swatch") };
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
            text.SetResourceReference(ForegroundProperty, valid ? "Ink" : "Bad");
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
        var panel = new StackPanel();
        panel.Children.Add(Hint("The chat bubble is shown next to the character while this page is open. Its outline and buttons " +
                                "take the character's Normal color (Character > Colors)."));

        panel.Children.Add(Heading("Theme"));
        panel.Children.Add(Hint("For the chat bubble, the menus and these settings."));
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
        var panel = new StackPanel();
        panel.Children.Add(Check("Voice on (push-to-talk and spoken replies)", edited.VoiceEnabled,
            value => edited.VoiceEnabled = value, needsRestart: true));

        panel.Children.Add(Heading("Which voice"));
        panel.Children.Add(Hint("The natural voices sound much more human. Each is a one-time download and runs on this computer."));
        var voice = new VoicePicker(host.Piper, edited.VoiceName, setting =>
        {
            edited.VoiceName = setting;
            Changed();
        });
        voice.Downloaded += () => host.TestVoice("Hi! This is my new voice.");
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

    UIElement PerformanceSection()
    {
        var panel = new StackPanel();
        PerformanceProfile recommended = PerformanceProfile.Recommend(Assistant.Core.Setup.HardwareCheck.Detect());
        panel.Children.Add(Hint($"How heavy the assistant may be on this PC. Recommended here: {recommended.Label}. " +
                                "Privacy is the same in all three: what's private stays on this computer."));
        foreach (PerformanceProfile profile in PerformanceProfile.All)
        {
            var option = new RadioButton
            {
                GroupName = "performance",
                IsChecked = edited.Performance == profile.Id,
                Margin = new Thickness(0, 4, 0, 2),
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Inlines =
                    {
                        new Run(profile.Label + (profile == recommended ? " (recommended)" : "")) { FontWeight = FontWeights.SemiBold },
                        new Run(": " + profile.Summary),
                    },
                },
            };
            option.Checked += (_, _) =>
            {
                if (edited.Performance == profile.Id) return;
                edited.Performance = profile.Id;
                Changed(needsRestart: true);
            };
            panel.Children.Add(option);
        }
        panel.Children.Add(Hint("Balanced and Full need the local model; if it isn't set up yet, tray > Run setup again downloads it."));
        panel.Children.Add(Heading("Right now"));
        panel.Children.Add(ResourceTable());
        return panel;
    }

    UIElement StartupSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Starting"));
        var startup = Check("Start with Windows (also in Task Manager > Startup apps)", edited.StartWithWindows,
            value => edited.StartWithWindows = value);
        startup.IsEnabled = host.Installed;
        panel.Children.Add(startup);
        if (!host.Installed) panel.Children.Add(Hint("Only the installed app can start with Windows, not a copy run from source."));
        panel.Children.Add(Check("Close while I play Riot games (quit as soon as the Riot Client, League or Valorant opens)",
            edited.QuitForRiotGames, value => edited.QuitForRiotGames = value));
        panel.Children.Add(Hint("So nothing of the assistant runs next to Riot Vanguard. It won't start while one of them is open, " +
                                "and it doesn't come back by itself: start it from the desktop icon when you're done playing."));

        return panel;
    }

    UIElement FeaturesSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Web search"));
        string[] engines = ["google", "duckduckgo", "bing"];
        var engine = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { "Google", "DuckDuckGo", "Bing" } };
        engine.SelectedIndex = Math.Max(0, Array.IndexOf(engines, edited.SearchEngine));
        engine.SelectionChanged += (_, _) => edited.SearchEngine = engines[Math.Max(0, engine.SelectedIndex)];
        panel.Children.Add(Labeled("Search with", engine));
        panel.Children.Add(Hint("For \"search the web for …\": the search opens in your browser. DuckDuckGo doesn't tie searches to you."));

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

        return panel;
    }

    UIElement CloudSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Smarter chat (optional, free)"));
        var hint = Hint("Plain conversation goes to a free cloud model, which is much smarter than the local one. Files, screenshots, " +
                        "activity, notes and memory never go there (except about-me facts, if you allow it below). Groq is recommended. ");
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
        panel.Children.Add(Check("Let cloud chat know about me: your name and the about-me facts that matter for the message",
            edited.CloudKnowsAboutMe, value => edited.CloudKnowsAboutMe = value));
        panel.Children.Add(Hint("Only lines from memory\\about-me.md, picked per message. Places, files, notes, activity and past " +
                                "conversations never go. Every request, with exactly what was sent, is in the sent folder."));

        return panel;
    }

    UIElement UpdatesSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Updates"));
        panel.Children.Add(Check("Check for a new version once a day and install it on the next start", edited.AutoUpdate,
            value => edited.AutoUpdate = value));
        panel.Children.Add(Hint($"This is version {host.Version}."));

        var updateStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0), MaxWidth = 300 };
        var checkNow = new Button { Content = "Check now", Padding = new Thickness(10, 2, 10, 2) };
        var install = new Button { Content = "Restart to update", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        void ShowPending()
        {
            string? pending = host.PendingUpdate();
            install.Visibility = pending is null ? Visibility.Collapsed : Visibility.Visible;
            if (pending is not null) updateStatus.Text = $"Version {pending} is ready to install.";
        }
        checkNow.Click += async (_, _) =>
        {
            checkNow.IsEnabled = false;
            updateStatus.Text = "Checking…";
            updateStatus.Text = await host.CheckForUpdates();
            checkNow.IsEnabled = true;
            ShowPending();
        };
        install.Click += (_, _) =>
        {
            // Keep the settings changes; the restart that installs the update applies them all.
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
            Close();
            host.RestartToUpdate();
        };
        ShowPending();
        var updateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        updateRow.Children.Add(checkNow);
        updateRow.Children.Add(install);
        updateRow.Children.Add(updateStatus);
        panel.Children.Add(updateRow);

        return panel;
    }

    UIElement DataSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Your data"));
        panel.Children.Add(Hint("Uninstalling the app keeps Documents\\Assistant (memory, settings, logs) in case you come back. " +
                                "To remove it all now, use this button. The assistant closes afterwards and starts fresh next time."));
        var delete = new Button { Content = "Delete all my data…", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left };
        delete.SetResourceReference(ForegroundProperty, "Bad");
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

    // What each part uses right now, refreshed every 2 seconds while Settings is open.
    UIElement ResourceTable()
    {
        var table = new Grid { Margin = new Thickness(0, 8, 0, 4) };
        foreach (double width in new[] { 170.0, 70, 60 }) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        async Task RefreshAsync()
        {
            IReadOnlyList<Assistant.Core.Diagnostics.ResourceUsage> usage;
            try
            {
                usage = await host.Resources();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return;
            }
            table.Children.Clear();
            table.RowDefinitions.Clear();
            AddRow(0, "Using now", "Memory", "CPU", "", header: true);
            for (int i = 0; i < usage.Count; i++)
            {
                var part = usage[i];
                AddRow(i + 1, part.Part, part.Running ? $"{part.MemoryMb:0} MB" : "–", part.Running ? $"{part.CpuPercent:0.0}%" : "–", part.Note);
            }
        }
        void AddRow(int row, string part, string memory, string cpu, string note, bool header = false)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            string[] cells = [part, memory, cpu, note];
            for (int column = 0; column < cells.Length; column++)
            {
                var cell = new TextBlock
                {
                    Text = cells[column],
                    FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 6, 1),
                };
                cell.SetResourceReference(TextBlock.ForegroundProperty, column == 3 ? "Muted" : "Ink");
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                table.Children.Add(cell);
            }
        }

        timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            timer.Start();
        };
        Closed += (_, _) => timer.Stop();
        return table;
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
        if (mood is not null && Enum.TryParse(mood, out Mood shown)) host.PreviewMood(shown);
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

    // A page: its name as a title, then the content, scrolling, at a comfortable reading width.
    void AddPage(TabControl tabs, string title, UIElement content)
    {
        var page = new StackPanel { Margin = new Thickness(28, 20, 28, 24), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        page.Children.Add(content);
        tabs.Items.Add(new TabItem
        {
            Header = title,
            Style = (Style)FindResource("SideNavItem"),
            Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        });
    }

    static StackPanel Stack(params UIElement[] parts)
    {
        var stack = new StackPanel();
        foreach (UIElement part in parts) stack.Children.Add(part);
        return stack;
    }

    static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 20, 0, 6) };

    static TextBlock Hint(string text)
    {
        var hint = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return hint;
    }

    static UIElement Labeled(string label, UIElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
        row.Children.Add(new TextBlock { Text = label, Width = 130, VerticalAlignment = VerticalAlignment.Center });
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
