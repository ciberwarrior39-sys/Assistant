using Assistant.Core.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Assistant.Core.Voice;

/// <summary>
/// The wake word's three models (openWakeWord's, in ONNX form) and where they live.
/// The two shared ones (sound to spectrogram, spectrogram to embedding) and the ready-made "Hey Jarvis" model are
/// downloaded once from openWakeWord's releases on GitHub to %LocalAppData%\Assistant\models\wake. The app's own
/// "Hey Assistant" model, once trained (docs\wake-word-training.md), is used instead: wake\hey_assistant.onnx next to
/// the app, or the same file dropped into that models folder.
/// Everything runs on the CPU on one thread, on this computer.
/// </summary>
public sealed class OnnxWakeWord : IDisposable
{
    const string Release = "https://github.com/dscripka/openWakeWord/releases/download/v0.5.1/";
    const string Spectrogram = "melspectrogram.onnx", Embedding = "embedding_model.onnx";
    const string OwnModel = "hey_assistant.onnx", ReadyMade = "hey_jarvis_v0.1.onnx";

    readonly InferenceSession spectrogram, embedding, wake;

    public WakeWordDetector Detector { get; }

    OnnxWakeWord(string folder, string wakeModel)
    {
        // One thread each: this runs all day in the background and must stay out of the way.
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
        spectrogram = new InferenceSession(Path.Combine(folder, Spectrogram), options);
        embedding = new InferenceSession(Path.Combine(folder, Embedding), options);
        wake = new InferenceSession(wakeModel, options);

        // How many embeddings the wake model looks at ([1, 16, 96] for the ready-made ones).
        int[] shape = wake.InputMetadata.Values.First().Dimensions;
        int scored = shape.Length == 3 && shape[1] > 0 ? shape[1] : 16;
        Detector = new WakeWordDetector(
            samples => Run(spectrogram, samples, [1, samples.Length]),
            strips => Run(embedding, strips, [1, WakeWordDetector.Strips, WakeWordDetector.Bands, 1]),
            embeddings => Run(wake, embeddings, [1, scored, WakeWordDetector.EmbeddingSize]) is [var first, ..] ? first : 0f,
            scored);
    }

    static float[] Run(InferenceSession session, float[] input, int[] shape)
    {
        string name = session.InputMetadata.Keys.First();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> output =
            session.Run(new[] { NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(input, shape)) });
        return output.First().AsEnumerable<float>().ToArray();
    }

    // ---- Where the models are -----------------------------------------------------------------------------------

    /// <summary>The app's own model if it is there, else the ready-made one (which may still need downloading).</summary>
    static string WakeModel(string folder)
    {
        string shipped = Path.Combine(AppContext.BaseDirectory, "wake", OwnModel), dropped = Path.Combine(folder, OwnModel);
        return File.Exists(shipped) ? shipped : File.Exists(dropped) ? dropped : Path.Combine(folder, ReadyMade);
    }

    /// <summary>What to say to wake it: "Hey Assistant" with the app's own model, "Hey Jarvis" with the ready-made one.</summary>
    public static string Phrase(string folder) =>
        Path.GetFileName(WakeModel(folder)) == OwnModel ? "Hey Assistant" : "Hey Jarvis";

    public static bool IsDownloaded(string folder) =>
        File.Exists(Path.Combine(folder, Spectrogram)) && File.Exists(Path.Combine(folder, Embedding)) && File.Exists(WakeModel(folder));

    /// <summary>Downloads what is missing (about 3 MB in all), each file to a temporary name first.</summary>
    public static async Task EnsureDownloadedAsync(string folder, HttpClient http, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var needed = new List<string> { Spectrogram, Embedding };
        if (Path.GetFileName(WakeModel(folder)) == ReadyMade) needed.Add(ReadyMade);
        foreach (string file in needed)
        {
            string target = Path.Combine(folder, file);
            if (File.Exists(target)) continue;
            DiagnosticLog.Info($"Downloading the wake word model {file}");
            string partial = target + ".download";
            await using (Stream source = await http.GetStreamAsync(Release + file, ct))
            await using (FileStream copy = File.Create(partial))
            {
                await source.CopyToAsync(copy, ct);
            }
            File.Move(partial, target, overwrite: true);
        }
    }

    /// <summary>Loads the models. Call after EnsureDownloadedAsync.</summary>
    public static OnnxWakeWord Load(string folder) => new(folder, WakeModel(folder));

    public void Dispose()
    {
        spectrogram.Dispose();
        embedding.Dispose();
        wake.Dispose();
    }
}
