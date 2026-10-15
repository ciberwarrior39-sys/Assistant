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
    readonly string? defaultVoice; // Windows' own choice, to go back to when the setting is cleared

    public event Action? Finished;

    public bool IsSpeaking { get; private set; }

    /// <param name="voiceName">Part of an installed voice's name, e.g. "Zira"; empty for Windows' default voice.</param>
    public Speaker(string voiceName, int rate)
    {
        synthesizer.SetOutputToDefaultAudioDevice();
        defaultVoice = synthesizer.Voice?.Name;
        Configure(voiceName, rate);
        synthesizer.SpeakCompleted += (_, e) =>
        {
            if (e.Prompt != current) return; // an earlier reply that was cut off
            current = null;
            IsSpeaking = false;
            Finished?.Invoke();
        };
    }

    /// <summary>Picks the voice (by part of its name; empty for Windows' default) and speed. Used live by Settings.</summary>
    public void Configure(string voiceName, int rate)
    {
        synthesizer.Rate = Math.Clamp(rate, -10, 10);
        InstalledVoice? match = voiceName.Length == 0 ? null : synthesizer.GetInstalledVoices()
            .FirstOrDefault(voice => voice.Enabled && voice.VoiceInfo.Name.Contains(voiceName, StringComparison.OrdinalIgnoreCase));
        if (match is not null) synthesizer.SelectVoice(match.VoiceInfo.Name);
        else
        {
            if (voiceName.Length > 0) DiagnosticLog.Info("Configured voice not installed; using the default voice");
            if (defaultVoice is not null) synthesizer.SelectVoice(defaultVoice);
        }
    }

    /// <summary>The Windows voices that can be chosen in Settings.</summary>
    public static IReadOnlyList<string> InstalledVoiceNames()
    {
        using var synthesizer = new SpeechSynthesizer();
        return synthesizer.GetInstalledVoices().Where(voice => voice.Enabled).Select(voice => voice.VoiceInfo.Name).ToList();
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
