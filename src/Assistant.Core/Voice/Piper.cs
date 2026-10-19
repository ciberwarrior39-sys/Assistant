using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Core.Diagnostics;
using Assistant.Core.Setup;
using NAudio.Wave;

namespace Assistant.Core.Voice;

/// <summary>A natural voice the app offers: its config id, what Settings shows, and the credit line.</summary>
public sealed record PiperVoice(string Id, string Label, string Credit, string RepoFolder)
{
    /// <summary>How config.json stores it in voiceName: "piper:en_US-ljspeech-medium".</summary>
    public string Setting => PiperVoices.Prefix + Id;
}

/// <summary>
/// The natural voices on offer. Only voices whose recordings are public domain (checked in each voice's
/// MODEL_CARD on huggingface.co/rhasspy/piper-voices), so they can be used and passed on freely.
/// More languages come later.
/// </summary>
public static class PiperVoices
{
    public const string Prefix = "piper:";

    public static readonly IReadOnlyList<PiperVoice> All =
    [
        new("en_US-ljspeech-medium", "Natural: US English, female", "LJ Speech recordings (public domain)", "en/en_US/ljspeech/medium"),
        new("en_US-john-medium", "Natural: US English, male", "\"John\", LibriVox recordings (public domain)", "en/en_US/john/medium"),
        new("en_GB-cori-medium", "Natural: UK English, female", "\"Cori\", LibriVox recordings (public domain)", "en/en_GB/cori/medium"),
    ];

    /// <summary>The Piper voice a voiceName setting means, or null for a Windows voice.</summary>
    public static PiperVoice? FromSetting(string voiceName) =>
        voiceName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? All.FirstOrDefault(voice => voice.Id.Equals(voiceName[Prefix.Length..], StringComparison.OrdinalIgnoreCase))
            : null;
}

/// <summary>
/// Piper's program and voices in %LocalAppData%\Assistant\piper, downloaded on request (Settings > Voice or the
/// setup wizard). Piper is the last Windows release of rhasspy/piper (MIT; the project was archived in 2025).
/// It runs as a separate program, so the GPL espeak-ng inside it stays separate from the app.
/// </summary>
public sealed class PiperInstall(string folder)
{
    const string EngineZip = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
    const string VoiceBase = "https://huggingface.co/rhasspy/piper-voices/resolve/main/";
    public const int EngineMegabytes = 22, VoiceMegabytes = 64;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    string EngineFolder => Path.Combine(folder, "engine");
    string VoiceFolder => Path.Combine(folder, "voices");

    /// <summary>piper.exe inside the unpacked zip (it has its own "piper" folder), or null before the download.</summary>
    public string? ExePath =>
        Directory.Exists(EngineFolder)
            ? Directory.EnumerateFiles(EngineFolder, "piper.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;

    public string ModelPath(PiperVoice voice) => Path.Combine(VoiceFolder, voice.Id + ".onnx");

    public bool IsReady(PiperVoice voice) =>
        ExePath is not null && File.Exists(ModelPath(voice)) && File.Exists(ModelPath(voice) + ".json");

    /// <summary>Megabytes still to download for this voice (the program the first time, then the voice).</summary>
    public int MegabytesNeeded(PiperVoice voice) =>
        (ExePath is null ? EngineMegabytes : 0) + (File.Exists(ModelPath(voice)) ? 0 : VoiceMegabytes);

    /// <summary>Downloads whatever this voice still needs: Piper itself the first time, then the voice.</summary>
    public async Task InstallAsync(PiperVoice voice, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        if (ExePath is null)
        {
            Directory.CreateDirectory(folder);
            string zip = Path.Combine(folder, "piper.zip");
            await Downloads.FileAsync(Http, new Uri(EngineZip), zip, "Downloading the voice program", progress, ct);
            progress.Report(new SetupProgress("Unpacking", 0, 0));
            if (Directory.Exists(EngineFolder)) Directory.Delete(EngineFolder, recursive: true);
            ZipFile.ExtractToDirectory(zip, EngineFolder);
            File.Delete(zip);
            if (ExePath is null) throw new IOException("piper.exe wasn't in the download.");
        }

        Directory.CreateDirectory(VoiceFolder);
        string model = ModelPath(voice);
        string source = VoiceBase + voice.RepoFolder + "/" + voice.Id + ".onnx";
        if (!File.Exists(model + ".json"))
            await Downloads.FileAsync(Http, new Uri(source + ".json"), model + ".json", "Downloading the voice", progress, ct);
        if (!File.Exists(model))
            await Downloads.FileAsync(Http, new Uri(source), model, $"Downloading the voice ({voice.Label.Replace("Natural: ", "")})", progress, ct);
    }

    /// <summary>The voice's sample rate from its .onnx.json (22050 for the voices offered).</summary>
    public int SampleRate(PiperVoice voice)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(ModelPath(voice) + ".json"));
            return json.RootElement.TryGetProperty("audio", out JsonElement audio)
                   && audio.TryGetProperty("sample_rate", out JsonElement rate) && rate.TryGetInt32(out int value)
                ? value
                : 22050;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return 22050;
        }
    }
}

/// <summary>
/// Speaks with Piper: the text goes to piper.exe one sentence per line, and the sound it writes back is played
/// as it arrives, so the first sentence starts while the rest is still being made. Nothing is written to disk.
/// </summary>
sealed partial class PiperSpeech : IDisposable
{
    readonly SynchronizationContext? ui = SynchronizationContext.Current; // events arrive where Speaker was made
    Process? process;
    WaveOutEvent? output;
    CancellationTokenSource? stop;

    /// <summary>The sentence-by-sentence playback ended by itself (not through Stop).</summary>
    public event Action? Finished;

    /// <summary>Piper produced no sound (crashed, or a bad voice file): the caller can say it another way.</summary>
    public event Action<string>? Failed;

    public void Speak(string exe, string model, int sampleRate, double lengthScale, string text)
    {
        Stop();
        var cancel = new CancellationTokenSource();
        stop = cancel;

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), // a BOM would be read aloud
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (string argument in new[] { "--model", model, "--output_raw", "--length_scale", lengthScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);

        Process piper;
        try
        {
            piper = Process.Start(start) ?? throw new InvalidOperationException("Piper didn't start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DiagnosticLog.Error("Piper didn't start", ex);
            Post(() => Failed?.Invoke(text));
            return;
        }
        process = piper;
        piper.ErrorDataReceived += (_, _) => { }; // Piper logs to stderr; it must be read or Piper stalls
        piper.BeginErrorReadLine();

        var buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromMinutes(10), // a long reply is still only a few MB
            ReadFully = true,                          // silence while waiting for the next sentence
        };
        var player = new WaveOutEvent();
        player.Init(buffer);
        output = player;

        _ = Task.Run(() => PumpAsync(piper, buffer, player, text, cancel.Token));
    }

    async Task PumpAsync(Process piper, BufferedWaveProvider buffer, WaveOutEvent player, string text, CancellationToken ct)
    {
        long total = 0;
        try
        {
            // One sentence per line: Piper finishes each line before reading the next.
            await piper.StandardInput.WriteAsync(string.Join("\n", Sentences(text)) + "\n");
            piper.StandardInput.Close();

            var chunk = new byte[8192];
            bool playing = false;
            int read;
            Stream sound = piper.StandardOutput.BaseStream;
            while ((read = await sound.ReadAsync(chunk, ct)) > 0)
            {
                buffer.AddSamples(chunk, 0, read);
                total += read;
                if (!playing)
                {
                    player.Play();
                    playing = true;
                }
            }

            // Everything is made; wait for the rest to be heard.
            while (!ct.IsCancellationRequested && buffer.BufferedBytes > 0) await Task.Delay(50, ct);
            await Task.Delay(150, ct); // the sound card's own buffer
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return; // stopped
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            DiagnosticLog.Error("Piper playback failed", ex);
        }

        if (ct.IsCancellationRequested) return;
        if (total == 0)
        {
            DiagnosticLog.Info("Piper made no sound; using the Windows voice");
            Post(() => Failed?.Invoke(text));
        }
        else
        {
            Post(() => Finished?.Invoke());
        }
        Post(() =>
        {
            if (stop?.Token == ct) Stop();
        });
    }

    /// <summary>Stops speaking now: the sound stops and Piper is ended. Raises nothing.</summary>
    public void Stop()
    {
        stop?.Cancel();
        stop = null;
        try
        {
            if (process is { HasExited: false }) process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
        process?.Dispose();
        process = null;
        output?.Stop();
        output?.Dispose();
        output = null;
    }

    void Post(Action action)
    {
        if (ui is null) action();
        else ui.Post(_ => action(), null);
    }

    // Lines for Piper: one sentence each, no line breaks inside (a list item is its own sentence).
    static IEnumerable<string> Sentences(string text) =>
        SentenceEnd().Split(text.Replace("\r", ""))
            .Select(sentence => sentence.Replace('\n', ' ').Trim())
            .Where(sentence => sentence.Length > 0);

    [GeneratedRegex(@"(?<=[.!?])\s+|\n+")]
    private static partial Regex SentenceEnd();

    public void Dispose() => Stop();
}
