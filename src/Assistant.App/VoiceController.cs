using System.IO;
using System.Net.Http;
using Assistant.Core;
using Assistant.Core.Diagnostics;
using Assistant.Core.Voice;

namespace Assistant.App;

/// <summary>
/// Push-to-talk: hold the keys and talk, let go and the words are transcribed on this computer,
/// asked like a typed question, and the reply is read aloud. Typed questions stay silent.
/// The first press downloads the speech model once.
/// </summary>
public sealed class VoiceController : IDisposable
{
    static readonly TimeSpan TooShort = TimeSpan.FromSeconds(0.4);

    readonly PushToTalkHotkey hotkey;
    readonly MicRecorder mic = new();
    readonly SpeechToText speechToText;
    readonly Speaker speaker;
    readonly ChatBubbleWindow bubble;
    readonly CharacterWindow character;
    readonly CharacterMood mood;
    bool working; // downloading or transcribing
    bool paused;

    public VoiceController(PushToTalkHotkey hotkey, SpeechToText speechToText, Speaker speaker,
                           ChatBubbleWindow bubble, CharacterWindow character, CharacterMood mood)
    {
        this.hotkey = hotkey;
        this.speechToText = speechToText;
        this.speaker = speaker;
        this.bubble = bubble;
        this.character = character;
        this.mood = mood;

        hotkey.Pressed += OnPressed;
        hotkey.Released += OnReleased;
        speaker.Finished += () =>
        {
            if (mood.Current == Mood.Speaking) mood.Set(Mood.Idle);
        };
        // Closing the bubble (Esc, clicking elsewhere) also stops the voice.
        bubble.IsVisibleChanged += (_, _) =>
        {
            if (!bubble.IsVisible) speaker.Stop();
        };
    }

    /// <summary>Game mode: the keys do nothing and anything being said or recorded stops.</summary>
    public bool Paused
    {
        get => paused;
        set
        {
            paused = value;
            if (!value) return;
            speaker.Stop();
            if (mic.IsRecording) mic.Stop().Wav.Dispose();
        }
    }

    async void OnPressed()
    {
        if (Paused || working || bubble.IsBusy) return;
        speaker.Stop(); // pressing again interrupts the reply

        if (!speechToText.IsModelDownloaded)
        {
            await DownloadModelAsync();
            return;
        }

        try
        {
            mic.Start();
            mood.Touch();
            mood.Set(Mood.Listening);
        }
        catch (InvalidOperationException ex)
        {
            DiagnosticLog.Error("Microphone unavailable", ex);
            Tell($"{ex.Message} Check that a microphone is connected and allowed in Windows' privacy settings.");
            mood.Set(Mood.Confused, TimeSpan.FromSeconds(4));
        }
    }

    async void OnReleased()
    {
        if (!mic.IsRecording) return;
        (MemoryStream wav, TimeSpan length) = mic.Stop();
        using (wav)
        {
            if (length < TooShort)
            {
                mood.Set(Mood.Idle); // a tap, not a question
                return;
            }

            string text;
            working = true;
            mood.Set(Mood.Thinking);
            try
            {
                text = await speechToText.TranscribeAsync(wav, CancellationToken.None);
                DiagnosticLog.Info($"Transcribed {length.TotalSeconds:0.0} s of speech"); // never the words
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DiagnosticLog.Error("Speech-to-text failed", ex);
                Tell("Speech recognition failed. Details are in the developer log.");
                mood.Set(Mood.Confused, TimeSpan.FromSeconds(4));
                return;
            }
            finally
            {
                working = false;
            }

            if (text.Length == 0)
            {
                mood.Set(Mood.Confused, TimeSpan.FromSeconds(3)); // "hmm?": nothing heard
                return;
            }

            character.ShowAgain();
            character.OpenChat();
            AssistantReply? reply = await bubble.AskSpokenAsync(text);
            if (reply is null || Paused) return;

            speaker.Speak(reply.Text);
            if (speaker.IsSpeaking) mood.Set(Mood.Speaking);
        }
    }

    async Task DownloadModelAsync()
    {
        working = true;
        mood.Set(Mood.Thinking);
        Tell($"Getting the speech model ready (about {speechToText.DownloadMegabytes} MB, one time only). This takes a minute or two...");
        try
        {
            await speechToText.EnsureModelAsync(CancellationToken.None);
            Tell($"Ready. Hold {hotkey.Keys} while you talk, and let go when you're done.");
            mood.Set(Mood.Happy, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            DiagnosticLog.Error("Whisper model download failed", ex);
            Tell("The speech model couldn't be downloaded. Check the internet connection and press the keys again.");
            mood.Set(Mood.Confused, TimeSpan.FromSeconds(4));
        }
        finally
        {
            working = false;
        }
    }

    /// <summary>Whisper is in memory (it leaves 90 seconds after the last question).</summary>
    public bool SpeechModelLoaded => speechToText.IsLoaded;

    /// <summary>Changes the voice and speed right away (Settings).</summary>
    public void ConfigureVoice(string voiceName, int rate) => speaker.Configure(voiceName, rate);

    /// <summary>Says something the user asked for in advance, like a timer, even in game mode.</summary>
    public void Announce(string text) => speaker.Speak(text);

    void Tell(string text)
    {
        character.ShowAgain();
        character.OpenChat();
        bubble.Say(text);
    }

    public void Dispose()
    {
        hotkey.Dispose();
        mic.Dispose();
        speechToText.Dispose();
        speaker.Dispose();
    }
}
