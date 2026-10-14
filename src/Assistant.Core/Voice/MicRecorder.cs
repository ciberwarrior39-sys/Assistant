using NAudio.Wave;

namespace Assistant.Core.Voice;

/// <summary>
/// Records the default microphone while push-to-talk is held: 16 kHz mono 16-bit, the format Whisper wants.
/// Audio stays in memory and is dropped after transcription; it is never written to disk.
/// </summary>
public sealed class MicRecorder : IDisposable
{
    public static readonly TimeSpan MaxLength = TimeSpan.FromSeconds(30);
    static readonly WaveFormat Format = new(16000, 16, 1);

    readonly Lock gate = new();
    MemoryStream? buffer;
    WaveInEvent? input;

    public bool IsRecording => input is not null;

    /// <summary>Starts recording. Throws InvalidOperationException when there is no usable microphone.</summary>
    public void Start()
    {
        if (input is not null) return;
        if (WaveInEvent.DeviceCount == 0) throw new InvalidOperationException("No microphone was found.");

        buffer = new MemoryStream();
        input = new WaveInEvent { WaveFormat = Format, BufferMilliseconds = 50 };
        input.DataAvailable += (_, e) =>
        {
            lock (gate)
            {
                // Stops growing at the maximum length; the user can't hold a key for long anyway.
                if (buffer is not null && buffer.Length < Format.AverageBytesPerSecond * MaxLength.TotalSeconds)
                    buffer.Write(e.Buffer, 0, e.BytesRecorded);
            }
        };
        try
        {
            input.StartRecording();
        }
        catch (NAudio.MmException ex)
        {
            Cleanup();
            throw new InvalidOperationException($"The microphone couldn't be opened ({ex.Result}).", ex);
        }
    }

    /// <summary>Stops and returns the recording as a WAV file in memory, and how long it is.</summary>
    public (MemoryStream Wav, TimeSpan Length) Stop()
    {
        if (input is null) return (new MemoryStream(), TimeSpan.Zero);
        input.StopRecording();

        byte[] pcm;
        lock (gate) pcm = buffer?.ToArray() ?? [];
        Cleanup();

        var length = TimeSpan.FromSeconds((double)pcm.Length / Format.AverageBytesPerSecond);
        return (ToWav(pcm), length);
    }

    void Cleanup()
    {
        input?.Dispose();
        input = null;
        lock (gate)
        {
            buffer?.Dispose();
            buffer = null;
        }
    }

    // A 44-byte WAV header in front of the raw samples.
    static MemoryStream ToWav(byte[] pcm)
    {
        var wav = new MemoryStream(44 + pcm.Length);
        using (var writer = new BinaryWriter(wav, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + pcm.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);                          // size of the format block
            writer.Write((short)1);                    // PCM
            writer.Write((short)Format.Channels);
            writer.Write(Format.SampleRate);
            writer.Write(Format.AverageBytesPerSecond);
            writer.Write((short)Format.BlockAlign);
            writer.Write((short)Format.BitsPerSample);
            writer.Write("data"u8);
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }
        wav.Position = 0;
        return wav;
    }

    public void Dispose() => Cleanup();
}
