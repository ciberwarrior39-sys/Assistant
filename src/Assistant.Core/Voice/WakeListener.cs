using System.Runtime.InteropServices;
using Assistant.Core.Diagnostics;
using NAudio.Wave;

namespace Assistant.Core.Voice;

/// <summary>
/// The always-listening half of the wake word: keeps the microphone open, passes the sound to the detector, and after
/// the phrase records what is said next until the speaker stops, then hands that over as a WAV file in memory.
/// Sound is never written to disk and never leaves the computer, and before the phrase nothing is kept at all.
/// While paused (a game, a locked PC, a call in another app) the microphone is closed, not just ignored.
/// </summary>
public sealed class WakeListener(WakeWordDetector detector) : IDisposable
{
    static readonly WaveFormat Format = new(16000, 16, 1);
    const int BufferMs = 80;
    const int MinSpeechMs = 300;     // less than this after the phrase: nothing was asked
    const int EndQuietMs = 900;      // this much quiet after speech: the question is over
    const int GiveUpMs = 4000;       // nothing said after the phrase
    const int MaxMs = 15000;
    const double MinLoud = 350;      // of 32768; quieter than this is never speech
    const double TargetLoud = 3000;  // how loud speech should reach the models: a quiet microphone is turned up to this
    const double MaxGain = 8;

    readonly Lock gate = new();
    WaveInEvent? input;
    MemoryStream? question;          // set while recording what follows the phrase
    int speechMs, quietMs, totalMs;
    double noise = 150;              // how loud the room is when nobody speaks, learned as it listens
    double peak = TargetLoud;        // the loudest it got lately; falls slowly, rises at once
    bool paused;
    int wakes;

    /// <summary>The phrase was heard; the question is being recorded now.</summary>
    public event Action? Woke;

    /// <summary>The question that followed the phrase, as a WAV file in memory, and how long it is.</summary>
    public event Action<MemoryStream, TimeSpan>? Heard;

    /// <summary>The phrase was heard, but nothing followed.</summary>
    public event Action? HeardNothing;

    /// <summary>Sound is dropped unheard while this says so (the app is talking, or push-to-talk is recording).</summary>
    public Func<bool>? Busy { get; set; }

    double loudest;
    float closest;

    /// <summary>
    /// For tuning: the loudest moment and the highest score since the last call. Numbers only; if the first stays
    /// near zero the microphone hears nothing, and if the second never rises the phrase isn't being recognised.
    /// </summary>
    public (double Loudest, float Closest, double Gain) TakeStats()
    {
        lock (gate)
        {
            (double, float, double) stats = (loudest, closest, Math.Clamp(TargetLoud / Math.Max(peak, 1), 1, MaxGain));
            loudest = 0;
            closest = 0;
            return stats;
        }
    }

    /// <summary>How many times the phrase was heard since the start (for the log: a count, never sound).</summary>
    public int Wakes => wakes;

    public bool IsListening => input is not null;

    /// <summary>Closes the microphone while true, and opens it again after.</summary>
    public bool Paused
    {
        get => paused;
        set
        {
            if (paused == value) return;
            paused = value;
            if (value) Close();
            else TryStart();
        }
    }

    /// <summary>Opens the microphone. False when there is none or it can't be opened (tried again on the next resume).</summary>
    public bool TryStart()
    {
        lock (gate)
        {
            if (input is not null || paused) return input is not null;
            if (WaveInEvent.DeviceCount == 0) return false;
            detector.Reset();
            var device = new WaveInEvent { WaveFormat = Format, BufferMilliseconds = BufferMs };
            device.DataAvailable += OnSound;
            try
            {
                device.StartRecording();
            }
            catch (NAudio.MmException ex)
            {
                device.Dispose();
                DiagnosticLog.Error("The wake word couldn't open the microphone", ex);
                return false;
            }
            input = device;
            return true;
        }
    }

    void Close()
    {
        WaveInEvent? device;
        lock (gate)
        {
            device = input;
            input = null;
            question?.Dispose();
            question = null;
        }
        if (device is null) return;
        device.DataAvailable -= OnSound;
        try
        {
            device.StopRecording();
        }
        catch (NAudio.MmException)
        {
            // Already stopped (the microphone was unplugged).
        }
        device.Dispose();
    }

    // Runs on the recorder's own thread, every 80 ms.
    void OnSound(object? sender, WaveInEventArgs e)
    {
        Action? tell = null;
        lock (gate)
        {
            if (input is null) return;
            ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded - e.BytesRecorded % 2));
            if (samples.Length == 0) return;
            int ms = samples.Length * 1000 / Format.SampleRate;
            double loud = Loudness(samples);

            // A quiet microphone (far away, or turned down in Windows) is turned up before the models hear it: they
            // were trained on speech at an ordinary level and score a faint "Hey Jarvis" just under the line.
            peak = Math.Max(loud, peak * 0.995);
            double gain = Math.Clamp(TargetLoud / Math.Max(peak, 1), 1, MaxGain);
            if (gain > 1.05) samples = Louder(samples, gain);

            if (question is null)
            {
                if (Busy?.Invoke() == true)
                {
                    detector.Reset(); // its own voice must not wake it
                    return;
                }
                if (loud < noise * 2) noise = noise * 0.98 + loud * 0.02; // the room's hum, slowly
                bool woke = detector.Feed(samples);
                loudest = Math.Max(loudest, loud);
                closest = Math.Max(closest, detector.LastScore);
                if (!woke) return;
                wakes++;
                question = new MemoryStream();
                speechMs = quietMs = totalMs = 0;
                tell = Woke;
            }
            else
            {
                question.Write(MemoryMarshal.AsBytes(samples));
                totalMs += ms;
                if (loud > Math.Max(MinLoud, noise * 3))
                {
                    speechMs += ms;
                    quietMs = 0;
                }
                else
                {
                    quietMs += ms;
                }

                bool asked = speechMs >= MinSpeechMs && (quietMs >= EndQuietMs || totalMs >= MaxMs);
                bool silent = speechMs < MinSpeechMs && totalMs >= GiveUpMs;
                if (!asked && !silent) return;

                byte[] pcm = question.ToArray();
                question.Dispose();
                question = null;
                detector.Reset();
                if (asked)
                {
                    var length = TimeSpan.FromMilliseconds(totalMs);
                    tell = () => Heard?.Invoke(MicRecorder.ToWav(pcm), length);
                }
                else
                {
                    tell = HeardNothing;
                }
            }
        }
        tell?.Invoke();
    }

    static short[] Louder(ReadOnlySpan<short> samples, double gain)
    {
        short[] louder = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            louder[i] = (short)Math.Clamp(samples[i] * gain, short.MinValue, short.MaxValue);
        return louder;
    }

    // Root mean square of the samples: 0 (silence) to 32768.
    static double Loudness(ReadOnlySpan<short> samples)
    {
        double sum = 0;
        foreach (short sample in samples) sum += (double)sample * sample;
        return Math.Sqrt(sum / samples.Length);
    }

    public void Dispose() => Close();
}
