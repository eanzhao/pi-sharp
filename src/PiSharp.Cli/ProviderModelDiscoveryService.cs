using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Ai;
using PiSharp.CodingAgent;

namespace PiSharp.Cli;

public sealed class ProviderModelDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public ProviderModelDiscoveryService(
        string cacheDirectory,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        _cacheDirectory = Path.GetFullPath(cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory)));
        _httpClient = httpClient ?? new HttpClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static TimeSpan CacheTtl => TimeSpan.FromMinutes(5);

    public async Task<ProviderModelListingResult> ListProviderModelsAsync(
        CodingAgentProviderFactory provider,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ProviderModelListingResult(provider.KnownModels);
        }

        var cachePath = GetCachePath(provider.Configuration.ProviderId.Value);
        var cacheEntry = await LoadCacheEntryAsync(cachePath, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        if (cacheEntry is not null && now - cacheEntry.FetchedAt < CacheTtl)
        {
            return new ProviderModelListingResult(MergeModels(provider, cacheEntry.Models));
        }

        try
        {
            var discoveredModels = await DiscoverModelsAsync(provider, apiKey, cancellationToken).ConfigureAwait(false);
            await SaveCacheEntryAsync(cachePath, new ProviderModelDiscoveryCacheEntry(now, discoveredModels.ToArray()), cancellationToken)
                .ConfigureAwait(false);
            return new ProviderModelListingResult(MergeModels(provider, discoveredModels));
        }
        catch (Exception exception) when (IsRecoverableDiscoveryFailure(exception))
        {
            if (cacheEntry is not null)
            {
                return new ProviderModelListingResult(
                    MergeModels(provider, cacheEntry.Models),
                    $"Warning: model discovery failed for provider '{provider.Configuration.ProviderId.Value}'; using cached results. {exception.Message}");
            }

            return new ProviderModelListingResult(
                provider.KnownModels,
                $"Warning: model discovery failed for provider '{provider.Configuration.ProviderId.Value}'; using static models. {exception.Message}");
        }
    }

    private async Task<IReadOnlyList<DiscoveredModelRecord>> DiscoverModelsAsync(
        CodingAgentProviderFactory provider,
        string apiKey,
        CancellationToken cancellationToken)
    {
        return provider.Configuration.ProviderId.Value.ToLowerInvariant() switch
        {
            "openai" => await DiscoverOpenAiModelsAsync(apiKey, cancellationToken).ConfigureAwait(false),
            "anthropic" => await DiscoverAnthropicModelsAsync(apiKey, cancellationToken).ConfigureAwait(false),
            "google" => await DiscoverGoogleModelsAsync(apiKey, cancellationToken).ConfigureAwait(false),
            _ => provider.KnownModels
                .Select(static model => new DiscoveredModelRecord(model.Id, model.Name, model.ContextWindow, model.MaxOutputTokens))
                .ToArray(),
        };
    }

    private async Task<IReadOnlyList<DiscoveredModelRecord>> DiscoverOpenAiModelsAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync(stream, ProviderModelDiscoveryJsonContext.Default.OpenAiModelsResponse, cancellationToken)
            .ConfigureAwait(false);

        return payload?.Data?
            .Where(static model => IsOpenAiChatModel(model.Id))
            .Select(static model => new DiscoveredModelRecord(model.Id, model.Id, null, null))
            .OrderBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<DiscoveredModelRecord>();
    }

    private async Task<IReadOnlyList<DiscoveredModelRecord>> DiscoverAnthropicModelsAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync(stream, ProviderModelDiscoveryJsonContext.Default.AnthropicModelsResponse, cancellationToken)
            .ConfigureAwait(false);

        return payload?.Data?
            .Where(static model => !string.IsNullOrWhiteSpace(model.Id) && model.Id.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            .Select(static model => new DiscoveredModelRecord(model.Id, model.DisplayName ?? model.Id, null, null))
            .OrderBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<DiscoveredModelRecord>();
    }

    private async Task<IReadOnlyList<DiscoveredModelRecord>> DiscoverGoogleModelsAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        var models = new List<DiscoveredModelRecord>();
        string? pageToken = null;

        do
        {
            var endpoint = pageToken is null
                ? $"https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000&key={Uri.EscapeDataString(apiKey)}"
                : $"https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000&pageToken={Uri.EscapeDataString(pageToken)}&key={Uri.EscapeDataString(apiKey)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync(stream, ProviderModelDiscoveryJsonContext.Default.GoogleModelsResponse, cancellationToken)
                .ConfigureAwait(false);

            if (payload?.Models is not null)
            {
                models.AddRange(payload.Models
                    .Where(static model => model.SupportedGenerationMethods?.Contains("generateContent", StringComparer.OrdinalIgnoreCase) == true)
                    .Select(static model =>
                    {
                        var id = ResolveGoogleModelId(model);
                        return new DiscoveredModelRecord(id, model.DisplayName ?? id, model.InputTokenLimit, model.OutputTokenLimit);
                    }));
            }

            pageToken = payload?.NextPageToken;
        } while (!string.IsNullOrWhiteSpace(pageToken));

        return models
            .Where(static model => !string.IsNullOrWhiteSpace(model.Id))
            .DistinctBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ModelMetadata> MergeModels(
        CodingAgentProviderFactory provider,
        IReadOnlyList<DiscoveredModelRecord> discoveredModels)
    {
        var merged = new Dictionary<string, ModelMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (var knownModel in provider.KnownModels)
        {
            merged[knownModel.Id] = knownModel;
        }

        foreach (var discoveredModel in discoveredModels)
        {
            if (merged.ContainsKey(discoveredModel.Id))
            {
                continue;
            }

            var fallback = provider.ResolveModel(discoveredModel.Id);
            merged[discoveredModel.Id] = fallback with
            {
                Name = string.IsNullOrWhiteSpace(discoveredModel.Name) ? fallback.Name : discoveredModel.Name,
                ContextWindow = discoveredModel.ContextWindow ?? fallback.ContextWindow,
                MaxOutputTokens = discoveredModel.MaxOutputTokens ?? fallback.MaxOutputTokens,
            };
        }

        return merged.Values
            .OrderBy(static model => model.ProviderId.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsOpenAiChatModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        if (modelId.Contains("embedding", StringComparison.OrdinalIgnoreCase) ||
            modelId.Contains("moderation", StringComparison.OrdinalIgnoreCase) ||
            modelId.Contains("whisper", StringComparison.OrdinalIgnoreCase) ||
            modelId.Contains("tts", StringComparison.OrdinalIgnoreCase) ||
            modelId.Contains("transcribe", StringComparison.OrdinalIgnoreCase) ||
            modelId.Contains("image", StringComparison.OrdinalIgnoreCase) ||
            modelId.Contains("realtime", StringComparison.OrdinalIgnoreCase) ||
            modelId.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return modelId.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("chatgpt-", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("codex", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o5", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("ft:gpt-", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("ft:o", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecoverableDiscoveryFailure(Exception exception) =>
        exception is HttpRequestException or IOException or TaskCanceledException or JsonException;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"Remote model discovery failed with status {(int)response.StatusCode} ({response.StatusCode}). {body.Trim()}",
            null,
            response.StatusCode);
    }

    private static string ResolveGoogleModelId(GoogleModelRecord model)
    {
        if (!string.IsNullOrWhiteSpace(model.BaseModelId))
        {
            return model.BaseModelId;
        }

        if (!string.IsNullOrWhiteSpace(model.Name) && model.Name.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            return model.Name["models/".Length..];
        }

        return model.Name ?? string.Empty;
    }

    private string GetCachePath(string providerId) =>
        Path.Combine(_cacheDirectory, $"{providerId}.json");

    private static async Task SaveCacheEntryAsync(
        string cachePath,
        ProviderModelDiscoveryCacheEntry cacheEntry,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(cachePath);
        await JsonSerializer.SerializeAsync(stream, cacheEntry, ProviderModelDiscoveryJsonContext.Default.ProviderModelDiscoveryCacheEntry, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ProviderModelDiscoveryCacheEntry?> LoadCacheEntryAsync(
        string cachePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(cachePath);
            return await JsonSerializer.DeserializeAsync(stream, ProviderModelDiscoveryJsonContext.Default.ProviderModelDiscoveryCacheEntry, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public sealed record ProviderModelListingResult(
        IReadOnlyList<ModelMetadata> Models,
        string? Warning = null);

    public sealed record DiscoveredModelRecord(
        string Id,
        string? Name,
        int? ContextWindow,
        int? MaxOutputTokens);

    public sealed record ProviderModelDiscoveryCacheEntry(
        DateTimeOffset FetchedAt,
        DiscoveredModelRecord[] Models);

    public sealed record OpenAiModelsResponse(OpenAiModelRecord[]? Data);

    public sealed record OpenAiModelRecord(string Id);

    public sealed record AnthropicModelsResponse(AnthropicModelRecord[]? Data);

    public sealed record AnthropicModelRecord(string Id, [property: JsonPropertyName("display_name")] string? DisplayName);

    public sealed record GoogleModelsResponse(
        GoogleModelRecord[]? Models,
        string? NextPageToken);

    public sealed record GoogleModelRecord(
        string? Name,
        string? DisplayName,
        string? BaseModelId,
        int? InputTokenLimit,
        int? OutputTokenLimit,
        string[]? SupportedGenerationMethods);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ProviderModelDiscoveryService.ProviderModelDiscoveryCacheEntry))]
[JsonSerializable(typeof(ProviderModelDiscoveryService.OpenAiModelsResponse))]
[JsonSerializable(typeof(ProviderModelDiscoveryService.AnthropicModelsResponse))]
[JsonSerializable(typeof(ProviderModelDiscoveryService.GoogleModelsResponse))]
internal sealed partial class ProviderModelDiscoveryJsonContext : JsonSerializerContext;
