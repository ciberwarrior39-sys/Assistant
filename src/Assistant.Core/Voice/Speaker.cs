using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Voice;

/// <summary>
/// Reads replies aloud with a voice built into Windows: nothing to download. Piper's more natural voices
/// can replace this class later (build step 10). Create it on the UI thread so Finished arrives there.
/// </summary>
public sealed partial class Speaker : IDisposable
{
    readonly SpeechSynthesizer synthesizer = new();
    Prompt? current;

    public event Action? Finished;

    public bool IsSpeaking { get; private set; }

    /// <param name="voiceName">Part of an installed voice's name, e.g. "Zira"; empty for Windows' default voice.</param>
    public Speaker(string voiceName, int rate)
    {
        synthesizer.SetOutputToDefaultAudioDevice();
        synthesizer.Rate = Math.Clamp(rate, -10, 10);
        if (voiceName.Length > 0)
        {
            InstalledVoice? match = synthesizer.GetInstalledVoices()
                .FirstOrDefault(voice => voice.Enabled && voice.VoiceInfo.Name.Contains(voiceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) synthesizer.SelectVoice(match.VoiceInfo.Name);
            else DiagnosticLog.Info("Configured voice not installed; using the default voice");
        }
        synthesizer.SpeakCompleted += (_, e) =>
        {
            if (e.Prompt != current) return; // an earlier reply that was cut off
            current = null;
            IsSpeaking = false;
            Finished?.Invoke();
        };
    }

    /// <summary>Speaks the text, interrupting anything still being said.</summary>
    public void Speak(string text)
    {
        string spoken = ForSpeech(text);
        if (spoken.Length == 0) return;
        Stop();
        IsSpeaking = true;
        current = synthesizer.SpeakAsync(spoken);
    }

    public void Stop()
    {
        if (!IsSpeaking) return;
        current = null;
        IsSpeaking = false;
        synthesizer.SpeakAsyncCancelAll();
        Finished?.Invoke();
    }

    // Paths are shown on the cards; read aloud they're just noise.
    static string ForSpeech(string text) => WindowsPath().Replace(text, "that location").Trim();

    [GeneratedRegex(@"[A-Za-z]:\\[^\s""',;()<>|?*]*")]
    private static partial Regex WindowsPath();

    public void Dispose() => synthesizer.Dispose();
}
