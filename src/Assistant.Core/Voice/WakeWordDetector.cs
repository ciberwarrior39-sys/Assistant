namespace Assistant.Core.Voice;

/// <summary>
/// Hears one phrase ("Hey Assistant") in a stream of microphone samples, the way openWakeWord does: every 80 ms of
/// sound becomes a strip of a spectrogram, the last 0.76 s of spectrogram becomes one "embedding" (96 numbers that
/// describe the sound), and the last 16 embeddings (1.3 s) are scored by the small model trained on the phrase.
/// The three models are passed in as plain functions, so the bookkeeping here can be checked without them.
/// Sound goes in and a yes/no comes out; nothing is kept beyond the last 1.3 seconds, in memory.
/// </summary>
public sealed class WakeWordDetector(Func<float[], float[]> spectrogram, Func<float[], float[]> embedding, Func<float[], float> score, int scoredEmbeddings = 16)
{
    /// <summary>Samples per step: 80 ms at 16 kHz.</summary>
    public const int Chunk = 1280;

    /// <summary>Earlier samples given to the spectrogram with each chunk, so the strips join up.</summary>
    public const int Overlap = 480;

    public const int Bands = 32;            // numbers per spectrogram strip
    public const int Strips = 76;           // strips one embedding looks at
    public const int EmbeddingSize = 96;

    static readonly int Cooldown = 25;      // chunks (2 s) before it can fire again

    readonly int scored = scoredEmbeddings; // how many embeddings the wake model looks at

    readonly float[] recent = new float[Chunk + Overlap];             // the newest samples, as the models want them (not scaled)
    readonly float[] strips = Filled(Strips * Bands, 1f);             // the newest spectrogram strips
    readonly float[] embeddings = new float[scoredEmbeddings * EmbeddingSize];
    readonly short[] waiting = new short[Chunk];                      // samples short of a full chunk
    int waitingCount;
    int embeddingsSeen;
    int sinceFired = Cooldown;

    /// <summary>How sure the model has to be, 0 to 1.</summary>
    public float Threshold { get; set; } = 0.5f;

    /// <summary>The newest score, for tuning.</summary>
    public float LastScore { get; private set; }

    /// <summary>Takes any number of samples. True when the phrase was just heard.</summary>
    public bool Feed(ReadOnlySpan<short> samples)
    {
        bool heard = false;
        foreach (short sample in samples)
        {
            waiting[waitingCount++] = sample;
            if (waitingCount < Chunk) continue;
            waitingCount = 0;
            heard |= Step();
        }
        return heard;
    }

    /// <summary>Forgets what it heard so far (after a wake, or a pause), so old sound can't trigger it.</summary>
    public void Reset()
    {
        Array.Clear(recent);
        Array.Fill(strips, 1f);
        Array.Clear(embeddings);
        waitingCount = 0;
        embeddingsSeen = 0;
        LastScore = 0;
    }

    bool Step()
    {
        // The newest chunk joins the end of the recent samples.
        Array.Copy(recent, Chunk, recent, 0, Overlap);
        for (int i = 0; i < Chunk; i++) recent[Overlap + i] = waiting[i];

        // New strips of spectrogram, scaled the way the embedding model was trained (x / 10 + 2).
        float[] fresh = spectrogram((float[])recent.Clone());
        int count = Math.Min(fresh.Length / Bands, Strips) * Bands;
        if (count == 0) return false;
        Array.Copy(strips, count, strips, 0, strips.Length - count);
        for (int i = 0; i < count; i++) strips[strips.Length - count + i] = fresh[fresh.Length - count + i] / 10f + 2f;

        float[] latest = embedding((float[])strips.Clone());
        if (latest.Length < EmbeddingSize) return false;
        Array.Copy(embeddings, EmbeddingSize, embeddings, 0, embeddings.Length - EmbeddingSize);
        Array.Copy(latest, 0, embeddings, embeddings.Length - EmbeddingSize, EmbeddingSize);
        embeddingsSeen++;
        sinceFired++;

        // Not before it has heard as much real sound as the model looks at.
        if (embeddingsSeen < scored) return false;
        LastScore = score((float[])embeddings.Clone());
        if (LastScore < Threshold || sinceFired < Cooldown) return false;
        sinceFired = 0;
        return true;
    }

    static float[] Filled(int length, float value)
    {
        float[] array = new float[length];
        Array.Fill(array, value);
        return array;
    }
}
