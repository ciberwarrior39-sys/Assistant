using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace Assistant.Core.Diagnostics;

/// <summary>One part of the assistant: memory in MB, CPU as a share of the whole processor, and a note.</summary>
public sealed record ResourceUsage(string Part, double MemoryMb, double CpuPercent, string Note)
{
    public bool Running => MemoryMb > 0;
}

/// <summary>
/// How much the assistant and the programs it starts are using, for Settings > Features and the developer log.
/// Memory is each process's private memory; CPU is measured between two calls (the first call shows 0).
/// Only these processes are looked at, and only their numbers are kept: nothing about other programs.
/// </summary>
public sealed class ResourceMonitor(HttpClient ollama)
{
    // Ollama's server and the runner that holds the model (its name changed between versions).
    static readonly string[] OllamaProcesses = ["ollama", "ollama_llama_server", "llama-server"];

    readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> previous = [];

    /// <param name="speechModelLoaded">Whether Whisper is in memory now (it lives inside the assistant's own process).</param>
    public async Task<IReadOnlyList<ResourceUsage>> SampleAsync(bool speechModelLoaded, CancellationToken ct)
    {
        var seen = new HashSet<int>();
        using Process self = Process.GetCurrentProcess();
        (double appMb, double appCpu) = Measure([self], seen);
        (double ollamaMb, double ollamaCpu) = Measure(OllamaProcesses.SelectMany(name => Process.GetProcessesByName(name)).ToList(), seen);
        (double piperMb, double piperCpu) = Measure(Process.GetProcessesByName("piper").ToList(), seen);
        (double everythingMb, double everythingCpu) = Measure(Process.GetProcessesByName("Everything").ToList(), seen);

        // Forget processes that ended, so the list doesn't grow.
        foreach (int gone in previous.Keys.Where(id => !seen.Contains(id)).ToList()) previous.Remove(gone);

        return
        [
            new("Assistant", appMb, appCpu, speechModelLoaded ? "speech model loaded" : "speech model not loaded"),
            new("Local model (Ollama)", ollamaMb, ollamaCpu, ollamaMb > 0 ? await LoadedModelAsync(ct) : "not running"),
            new("Natural voice (Piper)", piperMb, piperCpu, piperMb > 0 ? "speaking" : "only runs while speaking"),
            new("File search (Everything)", everythingMb, everythingCpu, everythingMb > 0 ? "" : "not running"),
        ];
    }

    /// <summary>One line for the developer log.</summary>
    public static string Describe(IReadOnlyList<ResourceUsage> usage) =>
        "Resources: " + string.Join(" | ", usage.Select(part =>
            part.Running ? $"{part.Part} {part.MemoryMb:0} MB {part.CpuPercent:0.0}% ({part.Note})" : $"{part.Part}: {part.Note}"));

    // Sums the processes, and disposes them.
    (double MemoryMb, double CpuPercent) Measure(IReadOnlyList<Process> processes, HashSet<int> seen)
    {
        double memory = 0, cpu = 0;
        DateTime now = DateTime.UtcNow;
        foreach (Process process in processes)
        {
            try
            {
                if (!seen.Add(process.Id)) continue;
                process.Refresh();
                memory += process.PrivateMemorySize64 / 1024.0 / 1024;
                TimeSpan used = process.TotalProcessorTime;
                if (previous.TryGetValue(process.Id, out var before) && now > before.At)
                    cpu += (used - before.Cpu).TotalMilliseconds / (now - before.At).TotalMilliseconds / Environment.ProcessorCount * 100;
                previous[process.Id] = (used, now);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Ended in the meantime, or belongs to another user: left out.
            }
            finally
            {
                if (process.Id != Environment.ProcessId) process.Dispose();
            }
        }
        return (memory, Math.Max(0, cpu));
    }

    // Ollama's own list of models in memory: name, size, and how much of it is on the graphics card.
    async Task<string> LoadedModelAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using HttpResponseMessage response = await ollama.GetAsync("api/ps", timeout.Token);
            if (!response.IsSuccessStatusCode) return "running";
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (!json.RootElement.TryGetProperty("models", out JsonElement models) || models.GetArrayLength() == 0)
                return "no model in memory";

            JsonElement model = models[0];
            string name = model.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "model" : "model";
            double size = model.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long bytes) ? bytes / 1e9 : 0;
            double vram = model.TryGetProperty("size_vram", out JsonElement v) && v.TryGetInt64(out long onCard) ? onCard / 1e9 : 0;
            string where = vram <= 0 ? "on the processor" : vram >= size * 0.95 ? "on the graphics card" : $"{vram:0.0} GB on the graphics card";
            return $"{name} loaded, {size:0.0} GB {where}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return "running";
        }
    }
}
