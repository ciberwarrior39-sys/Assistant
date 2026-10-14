using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Assistant.Core.Llm;
using Assistant.Core.Search;

namespace Assistant.Core;

/// <summary>Optional dependencies (the cloud providers) show as "not set up" instead of an error.</summary>
public sealed record DependencyStatus(string Name, bool Ready, string Detail)
{
    public bool Optional { get; init; }
}

/// <summary>
/// The one place that knows whether each external dependency is ready.
/// The setup wizard (build step 10) will plug in here to install what is missing.
/// </summary>
public sealed class DependencyService(
    HttpClient ollama,
    string model,
    IFileSearch fileSearch,
    IReadOnlyList<CloudEndpoint> cloudEndpoints,
    HttpClient cloudHttp)
{
    public async Task<IReadOnlyList<DependencyStatus>> CheckAllAsync(CancellationToken ct)
    {
        var statuses = new List<DependencyStatus> { await CheckOllamaAsync(ct), CheckFileSearch() };
        foreach (CloudEndpoint endpoint in cloudEndpoints) statuses.Add(await CheckCloudAsync(endpoint, ct));
        return statuses;
    }

    async Task<DependencyStatus> CheckOllamaAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using HttpResponseMessage response = await ollama.GetAsync("api/tags", timeout.Token);
            response.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));

            bool hasModel = document.RootElement.TryGetProperty("models", out JsonElement models)
                && models.ValueKind == JsonValueKind.Array
                && models.EnumerateArray().Any(m =>
                    m.TryGetProperty("name", out JsonElement name) && IsSameModel(name.GetString(), model));

            return hasModel
                ? new DependencyStatus("Ollama", true, model)
                : new DependencyStatus("Ollama", false, $"model missing, run: ollama pull {model}");
        }
        catch (HttpRequestException)
        {
            return new DependencyStatus("Ollama", false, "not running");
        }
        catch (OperationCanceledException)
        {
            return new DependencyStatus("Ollama", false, "not responding");
        }
    }

    DependencyStatus CheckFileSearch() =>
        fileSearch.IsAvailable(out string problem)
            ? new DependencyStatus(fileSearch.Name, true, "ready")
            : new DependencyStatus(fileSearch.Name, false, problem);

    // A cheap authenticated request that tells whether the key works, without using chat quota.
    async Task<DependencyStatus> CheckCloudAsync(CloudEndpoint endpoint, CancellationToken ct)
    {
        if (!endpoint.HasKey)
            return new DependencyStatus(endpoint.Name, false, "not set up (optional)") { Optional = true };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.BaseAddress, endpoint.KeyCheckPath));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
            using HttpResponseMessage response = await cloudHttp.SendAsync(request, timeout.Token);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new DependencyStatus(endpoint.Name, false, "key rejected, check config.json");
            if (response.StatusCode != HttpStatusCode.OK)
                return new DependencyStatus(endpoint.Name, false, $"error {(int)response.StatusCode}");
            if (endpoint.FallbackModels.Count == 0)
                return new DependencyStatus(endpoint.Name, true, endpoint.Model);

            // The key check returned the models this key may use: confirm the configured one is among them.
            IReadOnlyList<string> available = OpenAiCompatibleProvider.ParseModelIds(
                await response.Content.ReadAsStringAsync(timeout.Token));
            if (available.Contains(endpoint.Model))
                return new DependencyStatus(endpoint.Name, true, endpoint.Model);

            string? fallback = endpoint.FallbackModels.FirstOrDefault(model => available.Contains(model));
            return fallback is not null
                ? new DependencyStatus(endpoint.Name, true, $"{fallback} ({endpoint.Model} unavailable)")
                : new DependencyStatus(endpoint.Name, false, $"{endpoint.Model} unavailable, no fallback model");
        }
        catch (HttpRequestException)
        {
            return new DependencyStatus(endpoint.Name, false, "offline");
        }
        catch (OperationCanceledException)
        {
            return new DependencyStatus(endpoint.Name, false, "not responding");
        }
    }

    static bool IsSameModel(string? listed, string wanted) =>
        string.Equals(listed, wanted, StringComparison.OrdinalIgnoreCase)
        || string.Equals(listed, wanted + ":latest", StringComparison.OrdinalIgnoreCase);
}
