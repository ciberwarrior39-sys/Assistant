using System.IO;
using System.Net.Http;
using System.Threading;
using System.Text.Json;
using Assistant.Core;
using Assistant.Core.Diagnostics;
using Assistant.Core.Llm;
using Assistant.Core.Setup;
using Assistant.Core.Storage;

namespace Assistant.App;

/// <summary>
/// Trying the bigger local model from the offer card: download it, time it and the current one on this PC, and keep
/// the bigger one only if it's quick enough (<see cref="ModelCatalog.FastEnough"/>). Switching takes effect at once,
/// and only then is the old model deleted; otherwise the download is deleted and nothing changes. Models in an Ollama
/// the user installed themselves are never deleted: other programs may use them.
/// </summary>
public sealed class ModelSwitcher(AppConfig config, Action saveConfig, OllamaProvider localModel, DependencyService dependencies)
{
    public async Task TryAsync(ModelOfferItem card, CancellationToken ct)
    {
        string old = card.Current.Name, bigger = card.Bigger.Name;
        var url = new Uri(config.OllamaUrl.TrimEnd('/') + "/");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // a 4 GB download takes a while
        bool downloaded = false;
        try
        {
            if (!OllamaRuntime.HasModel(await OllamaRuntime.ModelsAsync(http, url, ct), bigger))
            {
                var progress = new Progress<SetupProgress>(step => card.Show(step.Stage, step.Fraction));
                card.Show($"Downloading {bigger}...", null);
                await OllamaRuntime.PullAsync(http, url, bigger, progress, ct);
                downloaded = true;
            }

            card.Show($"Timing {card.Current.Short} and {card.Bigger.Short} on this PC (about a minute)...", null);
            ModelTiming before = await OllamaRuntime.TimeAnswerAsync(http, url, old, config.ContextSize, ct);
            await OllamaRuntime.UnloadAsync(http, url, old, ct);
            ModelTiming after = await OllamaRuntime.TimeAnswerAsync(http, url, bigger, config.ContextSize, ct);
            DiagnosticLog.Info($"Model timing: {old} {before.Time.TotalSeconds:0.0}s, {bigger} {after.Time.TotalSeconds:0.0}s (answered: {after.Answered})");
            string times = $"{card.Bigger.Short} answers in {Seconds(after)} here, {card.Current.Short} in {Seconds(before)}";

            if (ModelCatalog.KeepBigger(after))
            {
                // It works: switch now, then free the old one.
                localModel.Model = bigger;
                dependencies.Model = bigger;
                config.Model = bigger;
                Offered(bigger);
                string removed = await RemoveAsync(http, url, old, card.Current, ct);
                card.Finish($"✓ Switched to {bigger}. {times}.{removed}");
                return;
            }

            await OllamaRuntime.UnloadAsync(http, url, bigger, ct);
            if (downloaded) await OllamaRuntime.DeleteAsync(http, url, bigger, ct);
            Offered(bigger);
            card.Finish(after.Answered
                ? $"Staying on {old}: {times}, and more than {ModelCatalog.FastEnough.TotalSeconds:0} seconds would make every " +
                  $"reply feel slow. {(downloaded ? "The download was deleted again." : "")}"
                : $"Staying on {old}: {bigger} didn't give an answer here. {(downloaded ? "The download was deleted again." : "")}");
        }
        catch (OperationCanceledException)
        {
            card.Finish($"Stopped. Still on {old}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException)
        {
            DiagnosticLog.Error("Trying the bigger model failed", ex);
            card.Finish($"That didn't work ({ex.Message}). Still on {old}, nothing changed. Is the internet on? " +
                        "The offer comes back next time the assistant starts.");
        }
    }

    // The old model, after switching: deleted from the app's own Ollama; left alone in the user's own one.
    async Task<string> RemoveAsync(HttpClient http, Uri url, string old, LocalModelInfo oldInfo, CancellationToken ct)
    {
        if (!config.OllamaEmbedded)
            return $" {old} is still in your own Ollama, since other programs may use it (\"ollama rm {old}\" removes it).";
        try
        {
            await OllamaRuntime.UnloadAsync(http, url, old, ct);
            await OllamaRuntime.DeleteAsync(http, url, old, ct);
            return $" {old} was deleted ({oldInfo.DownloadGb:0.0} GB freed).";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            DiagnosticLog.Error("Deleting the old model failed", ex);
            return $" {old} couldn't be deleted this time; it isn't used anymore.";
        }
    }

    void Offered(string model)
    {
        config.ModelOffersDone = [.. config.ModelOffersDone, model];
        saveConfig();
    }

    static string Seconds(ModelTiming timing) => $"{timing.Time.TotalSeconds:0.0} s";
}
