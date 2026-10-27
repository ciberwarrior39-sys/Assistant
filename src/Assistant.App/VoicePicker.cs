using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Assistant.Core.Diagnostics;
using Assistant.Core.Setup;
using Assistant.Core.Voice;

namespace Assistant.App;

/// <summary>
/// The voice list for Settings and the setup wizard: Windows' default, the natural Piper voices, then the other
/// Windows voices. A natural voice that isn't downloaded yet gets a Download button with a progress bar;
/// until then the Windows voice speaks.
/// </summary>
public sealed class VoicePicker : StackPanel
{
    sealed record Choice(string Setting, string Label, PiperVoice? Natural)
    {
        public override string ToString() => Label;
    }

    readonly PiperInstall piper;
    readonly Action<string> chosen;
    readonly Action<bool>? busy;
    readonly ComboBox box = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Button download = new() { Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
    readonly ProgressBar bar = new() { Height = 12, Width = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock credit = ThemeText.Muted(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
    bool downloading;

    /// <summary>A natural voice finished downloading (Settings offers a test then).</summary>
    public event Action? Downloaded;

    /// <param name="current">The voiceName setting now.</param>
    /// <param name="chosen">Called with the new voiceName setting.</param>
    /// <param name="busy">Told when a download starts and ends (the wizard locks its buttons).</param>
    public VoicePicker(PiperInstall piper, string current, Action<string> chosen, Action<bool>? busy = null)
    {
        this.piper = piper;
        this.chosen = chosen;
        this.busy = busy;

        var choices = new List<Choice> { new("", "Windows default", null) };
        choices.AddRange(PiperVoices.All.Select(voice => new Choice(voice.Setting, voice.Label, voice)));
        try
        {
            choices.AddRange(Speaker.InstalledVoiceNames().Select(name => new Choice(name, name, null)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No Windows speech engine: the natural voices and the default entry remain.
        }
        box.ItemsSource = choices;

        int selected = PiperVoices.FromSetting(current) is { } natural
            ? choices.FindIndex(choice => choice.Natural == natural)
            : current.Length == 0 ? 0 : choices.FindIndex(choice => choice.Natural is null && choice.Setting.Length > 0
                                                                   && choice.Setting.Contains(current, StringComparison.OrdinalIgnoreCase));
        box.SelectedIndex = Math.Max(0, selected);
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is Choice choice) this.chosen(choice.Setting);
            Refresh();
        };
        download.Click += async (_, _) => await DownloadAsync();

        Children.Add(box);
        Children.Add(download);
        Children.Add(bar);
        Children.Add(status);
        Children.Add(credit);
        Refresh();
    }

    PiperVoice? Natural => (box.SelectedItem as Choice)?.Natural;

    void Refresh()
    {
        if (Natural is not { } voice)
        {
            download.Visibility = Visibility.Collapsed;
            status.Text = "";
            credit.Text = "";
            return;
        }

        credit.Text = $"Voice: {voice.Credit}. Spoken by Piper (MIT license) on this computer; nothing is sent anywhere.";
        if (downloading) return;
        if (piper.IsReady(voice))
        {
            download.Visibility = Visibility.Collapsed;
            status.Text = "✓ Downloaded.";
        }
        else
        {
            download.Content = $"Download this voice ({piper.MegabytesNeeded(voice)} MB)";
            download.Visibility = Visibility.Visible;
            status.Text = "Until it's downloaded, the Windows voice speaks.";
        }
    }

    async Task DownloadAsync()
    {
        if (Natural is not { } voice || downloading) return;
        downloading = true;
        busy?.Invoke(true);
        download.IsEnabled = false;
        box.IsEnabled = false;
        bar.Visibility = Visibility.Visible;
        try
        {
            await piper.InstallAsync(voice, new Progress<SetupProgress>(Show), CancellationToken.None);
            status.Text = "✓ Downloaded.";
            chosen((box.SelectedItem as Choice)?.Setting ?? voice.Setting); // applies it now that it can speak
            Downloaded?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       or InvalidDataException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Piper voice download failed", ex);
            status.Text = "The download didn't work. Check the internet connection and try again.";
        }
        finally
        {
            downloading = false;
            bar.Visibility = Visibility.Collapsed;
            download.IsEnabled = true;
            box.IsEnabled = true;
            busy?.Invoke(false);
            if (!status.Text.StartsWith("The download", StringComparison.Ordinal)) Refresh();
        }
    }

    void Show(SetupProgress progress)
    {
        if (progress.Fraction is { } fraction)
        {
            bar.IsIndeterminate = false;
            bar.Maximum = 1;
            bar.Value = fraction;
            status.Text = $"{progress.Stage}: {progress.Done >> 20} MB of {progress.Total >> 20} MB";
        }
        else
        {
            bar.IsIndeterminate = true;
            status.Text = progress.Stage + "...";
        }
    }
}
