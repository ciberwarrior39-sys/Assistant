using System.Text;
using System.Text.RegularExpressions;
using Assistant.Core.Diagnostics;
using Whisper.net;
using Whisper.net.Ggml;

namespace Assistant.Core.Voice;

/// <summary>
/// Local Whisper (whisper.cpp through Whisper.net) on the CPU, so the GPU stays free for the language model.
/// The model (ggml-base.en, about 140 MB) downloads once to %LocalAppData%\Assistant\models on first use;
/// the installer will bundle it later (build step 10).
/// </summary>
public sealed partial class SpeechToText(string modelFolder) : IDisposable
{
    const GgmlType Model = GgmlType.BaseEn;
    const string ModelFile = "ggml-base.en.bin";

    readonly SemaphoreSlim gate = new(1, 1);
    WhisperFactory? factory;

    public string ModelPath => Path.Combine(modelFolder, ModelFile);
    public bool IsModelDownloaded => File.Exists(ModelPath);

    /// <summary>Downloads the model if it isn't there yet. Written to a temporary file first, so a broken download is never used.</summary>
    public async Task EnsureModelAsync(CancellationToken ct)
    {
        if (IsModelDownloaded) return;
        Directory.CreateDirectory(modelFolder);
        string partial = ModelPath + ".download";

        DiagnosticLog.Info("Downloading the Whisper model");
        await using (Stream source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(Model, cancellationToken: ct))
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
            WhisperFactory loaded = factory ??= WhisperFactory.FromPath(ModelPath); // kept loaded for the next question
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
        factory?.Dispose();
        gate.Dispose();
    }
}
