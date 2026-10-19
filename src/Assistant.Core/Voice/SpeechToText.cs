using System.Text;
using System.Text.RegularExpressions;
using Assistant.Core.Diagnostics;
using Whisper.net;
using Whisper.net.Ggml;

namespace Assistant.Core.Voice;

/// <summary>
/// Local Whisper (whisper.cpp through Whisper.net) on the CPU, so the GPU stays free for the language model.
/// The model (base.en, about 140 MB, or tiny.en, about 75 MB, by performance profile) downloads once to
/// %LocalAppData%\Assistant\models on first use. It leaves memory 90 seconds after the last question.
/// </summary>
public sealed partial class SpeechToText : IDisposable
{
    static readonly TimeSpan UnloadAfter = TimeSpan.FromSeconds(90);

    readonly string modelFolder;
    readonly GgmlType model;
    readonly string modelFile;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Timer unload;
    WhisperFactory? factory;

    /// <param name="modelName">base.en or tiny.en.</param>
    public SpeechToText(string modelFolder, string modelName = "base.en")
    {
        this.modelFolder = modelFolder;
        (model, modelFile, DownloadMegabytes) = modelName == "tiny.en"
            ? (GgmlType.TinyEn, "ggml-tiny.en.bin", 75)
            : (GgmlType.BaseEn, "ggml-base.en.bin", 140);
        unload = new Timer(_ => Unload(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>About how big the first download is.</summary>
    public int DownloadMegabytes { get; }

    public string ModelPath => Path.Combine(modelFolder, modelFile);
    public bool IsModelDownloaded => File.Exists(ModelPath);

    /// <summary>The model is in memory now (for the resource readout).</summary>
    public bool IsLoaded => factory is not null;

    /// <summary>Downloads the model if it isn't there yet. Written to a temporary file first, so a broken download is never used.</summary>
    public async Task EnsureModelAsync(CancellationToken ct)
    {
        if (IsModelDownloaded) return;
        Directory.CreateDirectory(modelFolder);
        string partial = ModelPath + ".download";

        DiagnosticLog.Info("Downloading the Whisper model");
        await using (Stream source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(model, cancellationToken: ct))
        await using (FileStream target = File.Create(partial))
        {
            await source.CopyToAsync(target, ct);
        }
        File.Move(partial, ModelPath, overwrite: true);
        DiagnosticLog.Info("Whisper model downloaded");
    }

    /// <summary>The words in a WAV recording, or "" if nothing was said. Runs on a background thread.</summary>
    public async Task<string> TranscribeAsync(Stream wav, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            unload.Change(Timeout.Infinite, Timeout.Infinite);
            WhisperFactory loaded = factory ??= WhisperFactory.FromPath(ModelPath); // kept loaded for a follow-up question
            return await Task.Run(async () =>
            {
                await using WhisperProcessor processor = loaded.CreateBuilder().WithLanguage("en").Build();
                var text = new StringBuilder();
                await foreach (var segment in processor.ProcessAsync(wav, ct))
                    text.Append(segment.Text);
                return Clean(text.ToString());
            }, ct);
        }
        finally
        {
            unload.Change(UnloadAfter, Timeout.InfiniteTimeSpan);
            gate.Release();
        }
    }

    // 90 seconds without a question: free the model's memory. It loads again (about a second) on the next one.
    void Unload()
    {
        if (!gate.Wait(0)) return; // transcribing right now
        try
        {
            factory?.Dispose();
            factory = null;
        }
        finally
        {
            gate.Release();
        }
    }

    // Whisper marks silence and noise as "[BLANK_AUDIO]", "(wind blowing)" and the like.
    static string Clean(string text) =>
        Spaces().Replace(NonSpeech().Replace(text, " "), " ").Trim();

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")]
    private static partial Regex NonSpeech();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public void Dispose()
    {
        unload.Dispose();
        factory?.Dispose();
        gate.Dispose();
    }
}
