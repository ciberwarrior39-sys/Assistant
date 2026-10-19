using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Voice;

/// <summary>
/// Reads replies aloud: with a natural Piper voice when one is chosen and downloaded, otherwise with a voice
/// built into Windows (nothing to download). If Piper fails, the Windows voice says it instead.
/// Create it on the UI thread so Finished arrives there.
/// </summary>
public sealed partial class Speaker : IDisposable
{
    readonly SpeechSynthesizer synthesizer = new();
    readonly PiperInstall? piper;
    readonly PiperSpeech natural = new();
    Prompt? current;
    readonly string? defaultVoice; // Windows' own choice, to go back to when the setting is cleared
    PiperVoice? piperVoice;
    int rate;

    public event Action? Finished;

    public bool IsSpeaking { get; private set; }

    /// <param name="voiceName">"piper:..." for a natural voice, part of an installed Windows voice's name (e.g. "Zira"),
    /// or empty for Windows' default voice.</param>
    public Speaker(string voiceName, int rate, PiperInstall? piper = null)
    {
        this.piper = piper;
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
        natural.Finished += () =>
        {
            if (!IsSpeaking) return;
            IsSpeaking = false;
            Finished?.Invoke();
        };
        natural.Failed += text =>
        {
            if (IsSpeaking) SpeakWithWindows(text); // still heard, just less natural
        };
    }

    /// <summary>Picks the voice and speed. Used live by Settings.</summary>
    public void Configure(string voiceName, int rate)
    {
        this.rate = Math.Clamp(rate, -10, 10);
        synthesizer.Rate = this.rate;
        piperVoice = PiperVoices.FromSetting(voiceName);
        if (piperVoice is not null) voiceName = ""; // the Windows default stands in if Piper can't speak
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

        if (piperVoice is { } voice && piper is not null && piper.IsReady(voice) && piper.ExePath is { } exe)
        {
            IsSpeaking = true;
            natural.Speak(exe, piper.ModelPath(voice), piper.SampleRate(voice), LengthScale, spoken);
            return;
        }
        SpeakWithWindows(spoken);
    }

    void SpeakWithWindows(string text)
    {
        IsSpeaking = true;
        current = synthesizer.SpeakAsync(text);
    }

    // Piper's speed: above 1 is slower. The -10..10 speed setting maps to 1.5..0.5.
    double LengthScale => Math.Clamp(1.0 - rate * 0.05, 0.5, 1.5);

    public void Stop()
    {
        if (!IsSpeaking) return;
        current = null;
        IsSpeaking = false;
        synthesizer.SpeakAsyncCancelAll();
        natural.Stop();
        Finished?.Invoke();
    }

    // Paths are shown on the cards; read aloud they're just noise.
    static string ForSpeech(string text) => WindowsPath().Replace(text, "that location").Trim();

    [GeneratedRegex(@"[A-Za-z]:\\[^\s""',;()<>|?*]*")]
    private static partial Regex WindowsPath();

    public void Dispose()
    {
        natural.Dispose();
        synthesizer.Dispose();
    }
}
