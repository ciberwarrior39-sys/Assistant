using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core.Characters;
using Assistant.Core.Diagnostics;
using Microsoft.Win32;

namespace Assistant.App;

/// <summary>Export: who made it, a short description, whether to include the glow colors, then where to save.</summary>
public sealed class ExportCharacterWindow : Window
{
    public ExportCharacterWindow(CharacterPacks packs, string name, IReadOnlyDictionary<string, string> colors)
    {
        Title = $"Export {name}";
        Icon = Shell.WindowIcon;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontSize = 13;
        FontFamily = Theme.UiFont;
        SetResourceReference(BackgroundProperty, "Paper");
        SetResourceReference(ForegroundProperty, "Ink");

        CharacterInfo info = packs.ReadInfo(name);
        var author = new TextBox { Text = info.Author, MaxLength = 60, Margin = new Thickness(0, 2, 0, 8) };
        var description = new TextBox { Text = info.Description, MaxLength = 300, TextWrapping = TextWrapping.Wrap, Height = 54, AcceptsReturn = false, Margin = new Thickness(0, 2, 0, 8) };
        var includeColors = new CheckBox { Content = "Include my glow colors (they can choose whether to use them)", IsChecked = true, Margin = new Thickness(0, 0, 0, 12) };
        var export = new Button { Content = "Export…", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };

        export.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save the character as one file",
                FileName = name + CharacterSharing.Extension,
                Filter = $"Assistant character (*{CharacterSharing.Extension})|*{CharacterSharing.Extension}",
                DefaultExt = CharacterSharing.Extension,
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                packs.WriteInfo(name, info with { Author = author.Text.Trim(), Description = description.Text.Trim() });
                CharacterSharing.Export(packs, name, includeColors.IsChecked == true ? colors : null, dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"Couldn't export: {ex.Message}", "Assistant");
                return;
            }
            Close();
            Shell.RevealFile(dialog.FileName); // ready to send
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Saves the character as one file you can send to anyone with the assistant.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Made by (optional)" });
        panel.Children.Add(author);
        panel.Children.Add(new TextBlock { Text = "Description (optional)" });
        panel.Children.Add(description);
        panel.Children.Add(includeColors);
        panel.Children.Add(export);
        Content = panel;
    }
}

/// <summary>Import preview: the Idle picture, name, maker and how complete it is, before anything is unpacked.</summary>
public sealed class ImportCharacterWindow : Window
{
    /// <summary>After Import: the new character's folder name, and the colors to use (null: keep yours).</summary>
    public string? ImportedName { get; private set; }
    public IReadOnlyDictionary<string, string>? Colors { get; private set; }

    public ImportCharacterWindow(CharacterPacks packs, string file, SharedCharacter shared)
    {
        Title = "Import a character";
        Icon = Shell.WindowIcon;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 13;
        FontFamily = Theme.UiFont;
        SetResourceReference(BackgroundProperty, "Paper");
        SetResourceReference(ForegroundProperty, "Ink");

        var preview = new Border
        {
            Width = 110,
            Height = 110,
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x34, 0x40)), // like a desktop, so transparency shows
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 14, 0),
            Child = new Image { Source = Picture(shared.Idle), Stretch = Stretch.Uniform, Margin = new Thickness(6) },
        };

        var details = new StackPanel { Width = 260 };
        details.Children.Add(new TextBlock { Text = shared.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (shared.Info.Author.Length > 0) details.Children.Add(ThemeText.Muted(new TextBlock { Text = "by " + shared.Info.Author }));
        if (shared.Info.Description.Length > 0)
            details.Children.Add(new TextBlock { Text = shared.Info.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        details.Children.Add(new TextBlock
        {
            Text = $"{shared.MoodsFilled} of {CharacterPacks.Slots.Count} moods have their own animation.",
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        if (shared.Idle is null)
            details.Children.Add(ThemeText.Muted(new TextBlock { Text = "It has no Normal animation, so you'll see a placeholder until you add one.", TextWrapping = TextWrapping.Wrap }, "Warn"));
        if (shared.Skipped > 0)
            details.Children.Add(ThemeText.Muted(new TextBlock { Text = $"{shared.Skipped} file(s) in it aren't animations and will be left out.", TextWrapping = TextWrapping.Wrap }));

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(preview);
        top.Children.Add(details);

        var useColors = new CheckBox { Content = "Use its glow colors (replaces your mood colors)", Margin = new Thickness(0, 12, 0, 0), Visibility = shared.Info.Colors.Count > 0 ? Visibility.Visible : Visibility.Collapsed };
        var import = new Button { Content = "Import", Padding = new Thickness(16, 4, 16, 4), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        import.Click += (_, _) =>
        {
            try
            {
                ImportedName = CharacterSharing.Import(packs, file);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                DiagnosticLog.Error("Character import failed", ex);
                MessageBox.Show(this, $"Couldn't import it: {ex.Message}", "Assistant");
                return;
            }
            if (useColors.IsChecked == true) Colors = shared.Info.Colors;
            DialogResult = true;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(import);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(top);
        panel.Children.Add(useColors);
        panel.Children.Add(buttons);
        Content = panel;
    }

    /// <summary>
    /// Reads the file and shows the preview. Returns the import result, or null if it was cancelled or isn't a
    /// character file (the user is told why).
    /// </summary>
    public static (string Name, IReadOnlyDictionary<string, string>? Colors)? Run(Window? owner, CharacterPacks packs, string file)
    {
        SharedCharacter shared;
        try
        {
            shared = CharacterSharing.Inspect(file);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageBox.Show($"\"{Path.GetFileName(file)}\" isn't a character file this app can read.\n\n{ex.Message}", "Assistant");
            return null;
        }

        var window = new ImportCharacterWindow(packs, file, shared);
        if (owner is { IsLoaded: true }) window.Owner = owner;
        return window.ShowDialog() == true && window.ImportedName is { } name ? (name, window.Colors) : null;
    }

    static BitmapSource? Picture(byte[]? bytes)
    {
        if (bytes is null) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelHeight = 200;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException or IOException)
        {
            return null; // not a valid picture: the preview stays empty
        }
    }
}

/// <summary>
/// Double-clicking a .assistant-character file: Windows starts the app with "--import file". If the assistant is
/// already running, that second start hands the file to it over a pipe only this Windows user can open, and exits.
/// </summary>
static class ImportHandoff
{
    public const string Argument = "--import";

    static string PipeName => $"Assistant.Import.{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";

    /// <summary>The file after --import, if the app was started to import one.</summary>
    public static string? FileFromArguments(string[] args)
    {
        int at = Array.IndexOf(args, Argument);
        return at >= 0 && at + 1 < args.Length && File.Exists(args[at + 1]) ? args[at + 1] : null;
    }

    /// <summary>Second start: give the file to the running assistant. False if it couldn't be reached.</summary>
    public static bool SendToRunning(string file)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            byte[] bytes = Encoding.UTF8.GetBytes(file);
            client.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Couldn't hand the character file to the running assistant", ex);
            return false;
        }
    }

    /// <summary>First start: waits for files from later starts, for as long as the app runs. Calls back on the UI thread.</summary>
    public static void Listen(Action<string> received)
    {
        var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync();
                    using var memory = new MemoryStream();
                    var buffer = new byte[4096];
                    int read;
                    while ((read = await server.ReadAsync(buffer)) > 0 && memory.Length < 8192) memory.Write(buffer, 0, read);
                    string file = Encoding.UTF8.GetString(memory.ToArray());
                    if (File.Exists(file)) _ = ui.InvokeAsync(() => received(file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Error("Character import pipe failed", ex);
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        });
    }
}

/// <summary>
/// The .assistant-character file type, for the current Windows user only (no admin rights): its icon is the app's,
/// and opening it imports the character. Installed app only; removed on uninstall.
/// </summary>
static class CharacterFileType
{
    const string ProgId = "DesktopAssistant.Character";
    const string Classes = @"Software\Classes\";

    public static void Register()
    {
        if (!StartWithWindows.IsInstalled || Environment.ProcessPath is not { } exe) return;
        try
        {
            string command = $"\"{exe}\" {ImportHandoff.Argument} \"%1\"";
            using (RegistryKey open = Registry.CurrentUser.CreateSubKey(Classes + ProgId + @"\shell\open\command"))
            {
                if (open.GetValue("") as string == command) return; // already up to date
                open.SetValue("", command);
            }
            using (RegistryKey type = Registry.CurrentUser.CreateSubKey(Classes + ProgId)) type.SetValue("", "Assistant character");
            using (RegistryKey icon = Registry.CurrentUser.CreateSubKey(Classes + ProgId + @"\DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (RegistryKey extension = Registry.CurrentUser.CreateSubKey(Classes + CharacterSharing.Extension)) extension.SetValue("", ProgId);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // Explorer picks up the new icon
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            DiagnosticLog.Error("Couldn't register the character file type", ex);
        }
    }

    public static void Remove()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(Classes + ProgId, throwOnMissingSubKey: false);
            using RegistryKey? extension = Registry.CurrentUser.OpenSubKey(Classes + CharacterSharing.Extension, writable: true);
            if (extension?.GetValue("") as string == ProgId) Registry.CurrentUser.DeleteSubKeyTree(Classes + CharacterSharing.Extension, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            // Uninstalling must not fail because of it.
        }
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}

static class ThemeText
{
    /// <summary>Gives a text the theme's muted (or another) color, which follows light and dark.</summary>
    public static TextBlock Muted(TextBlock text, string key = "Muted")
    {
        text.SetResourceReference(TextBlock.ForegroundProperty, key);
        return text;
    }
}
