using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Setup;

/// <summary>One step of a download: what's happening and how far along (Total 0 = unknown).</summary>
public readonly record struct SetupProgress(string Stage, long Done, long Total)
{
    public double? Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : null;
}

/// <summary>
/// The app's own copy of Ollama, so nobody installs anything by hand (setup plan, part 3). Ollama's standalone
/// Windows zip is unpacked into %LocalAppData%\Assistant\ollama and started as a child process on its own port,
/// with its models in the app's folder, so it never clashes with an Ollama the user installed and uninstalling
/// removes it. Environment variables are set only for that process.
/// </summary>
public sealed class OllamaRuntime(HttpClient http)
{
    public const int Port = 11435;
    public static readonly Uri EmbeddedUrl = new($"http://127.0.0.1:{Port}/");
    public static readonly Uri UsualUrl = new("http://localhost:11434/");

    const string ReleaseBase = "https://github.com/ollama/ollama/releases/latest/download/";

    static string AppFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Assistant");
    public static string Folder => Path.Combine(AppFolder, "ollama");
    public static string ModelsFolder => Path.Combine(AppFolder, "ollama-models");
    static string Exe => Path.Combine(Folder, "ollama.exe");

    Process? process;

    public static bool IsInstalled => File.Exists(Exe);

    /// <summary>Downloads and unpacks Ollama (plus the AMD package when needed). Replaces any earlier copy.</summary>
    public async Task InstallAsync(bool rocm, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        string temp = Path.Combine(Path.GetTempPath(), "Assistant-setup");
        Directory.CreateDirectory(temp);
        var packages = new List<string> { "ollama-windows-amd64.zip" };
        if (rocm) packages.Add("ollama-windows-amd64-rocm.zip");

        foreach (string package in packages)
        {
            string zip = Path.Combine(temp, package);
            await Downloads.FileAsync(http, new Uri(ReleaseBase + package), zip, $"Downloading {package}", progress, ct);

            progress.Report(new SetupProgress($"Unpacking {package}", 0, 0));
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Folder);
                ZipFile.ExtractToDirectory(zip, Folder, overwriteFiles: true);
            }, ct);
            File.Delete(zip);
        }
        DiagnosticLog.Info("Embedded Ollama installed");
    }

    /// <summary>Starts the embedded Ollama unless it's already answering (for example from an earlier run).</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (await IsAnsweringAsync(http, EmbeddedUrl, ct)) return;
        if (!IsInstalled) throw new InvalidOperationException("The app's Ollama isn't installed. Run setup again from the tray menu.");

        Directory.CreateDirectory(ModelsFolder);
        var start = new ProcessStartInfo(Exe, "serve")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Folder,
        };
        start.Environment["OLLAMA_HOST"] = $"127.0.0.1:{Port}";
        start.Environment["OLLAMA_MODELS"] = ModelsFolder;
        process = Process.Start(start);
        DiagnosticLog.Info("Embedded Ollama started");

        // Ready within a few seconds normally; slower disks get up to 30.
        for (int i = 0; i < 60; i++)
        {
            if (await IsAnsweringAsync(http, EmbeddedUrl, ct)) return;
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("The app's Ollama didn't start.");
    }

    /// <summary>Stops the embedded Ollama when the app quits.</summary>
    public void Stop()
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        process = null;
    }

    public static async Task<bool> IsAnsweringAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using HttpResponseMessage response = await http.GetAsync(new Uri(url, "api/tags"), timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The models an Ollama server already has ("qwen3.5:2b", ...). Empty if it isn't running.</summary>
    public static async Task<IReadOnlyList<string>> ModelsAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(new Uri(url, "api/tags"), ct);
            if (!response.IsSuccessStatusCode) return [];
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array
                ? models.EnumerateArray()
                    .Select(model => model.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "")
                    .Where(name => name.Length > 0)
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return [];
        }
    }

    public static bool HasModel(IReadOnlyList<string> models, string model) =>
        models.Any(name => string.Equals(name, model, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(name, model + ":latest", StringComparison.OrdinalIgnoreCase));

    /// <summary>Downloads a model into an Ollama server, reporting Ollama's own progress lines.</summary>
    public static async Task PullAsync(HttpClient http, Uri url, string model, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, "api/pull"))
        {
            Content = JsonContent.Create(new { model, stream = true }),
        };
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error))
                throw new InvalidOperationException($"Ollama couldn't download {model}: {error.GetString()}");

            string status = root.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? "" : "";
            long total = root.TryGetProperty("total", out JsonElement t) && t.TryGetInt64(out long tv) ? tv : 0;
            long done = root.TryGetProperty("completed", out JsonElement c) && c.TryGetInt64(out long cv) ? cv : 0;
            progress.Report(new SetupProgress(status.StartsWith("pulling", StringComparison.Ordinal)
                ? $"Downloading {model}" : Capitalize(status), done, total));
        }
    }

    static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>Plain file downloads with progress, written to a temporary name first so a broken download is never used.</summary>
public static class Downloads
{
    public static async Task FileAsync(HttpClient http, Uri source, string target, string stage, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        using HttpResponseMessage response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;

        string partial = target + ".download";
        await using (Stream input = await response.Content.ReadAsStreamAsync(ct))
        await using (FileStream output = File.Create(partial))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            var lastReport = Stopwatch.StartNew();
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (lastReport.ElapsedMilliseconds >= 150)
                {
                    progress.Report(new SetupProgress(stage, done, total));
                    lastReport.Restart();
                }
            }
            progress.Report(new SetupProgress(stage, done, total));
        }
        File.Move(partial, target, overwrite: true);
    }
}
