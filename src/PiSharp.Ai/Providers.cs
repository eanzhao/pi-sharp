using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace PiSharp.Ai;

public readonly record struct ApiId(string Value)
{
    public static readonly ApiId OpenAi = new("openai");
    public static readonly ApiId Anthropic = new("anthropic");
    public static readonly ApiId Google = new("google");

    public override string ToString() => Value;

    public static implicit operator ApiId(string value) => new(value);

    public static implicit operator string(ApiId value) => value.Value;
}

public readonly record struct ProviderId(string Value)
{
    public static readonly ProviderId OpenAi = new("openai");
    public static readonly ProviderId Anthropic = new("anthropic");
    public static readonly ProviderId Google = new("google");

    public override string ToString() => Value;

    public static implicit operator ProviderId(string value) => new(value);

    public static implicit operator string(ProviderId value) => value.Value;
}

public sealed record ProviderConfiguration(
    ProviderId ProviderId,
    ApiId ApiId,
    string DisplayName,
    Uri? Endpoint = null,
    string? DefaultModelId = null,
    string? ApiKeyEnvironmentVariable = null,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ProviderRegistration(
    ProviderConfiguration Configuration,
    IChatClient ChatClient)
{
    public ProviderId ProviderId => Configuration.ProviderId;

    public ApiId ApiId => Configuration.ApiId;
}

public sealed class ProviderRegistry
{
    private readonly ConcurrentDictionary<ProviderId, ProviderRegistration> _providers = new();

    public ProviderRegistration Register(ProviderConfiguration configuration, IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        return Register(new ProviderRegistration(configuration, chatClient));
    }

    public ProviderRegistration Register(ProviderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _providers[registration.ProviderId] = registration;
        return registration;
    }

    public bool TryGet(ProviderId providerId, [NotNullWhen(true)] out ProviderRegistration? registration) =>
        _providers.TryGetValue(providerId, out registration);

    public ProviderRegistration GetRequired(ProviderId providerId)
    {
        if (TryGet(providerId, out var registration))
        {
            return registration;
        }

        throw new KeyNotFoundException($"No provider is registered for '{providerId.Value}'.");
    }

    public IReadOnlyCollection<ProviderRegistration> GetByApi(ApiId apiId) =>
        _providers.Values
            .Where(registration => registration.ApiId == apiId)
            .OrderBy(registration => registration.ProviderId.Value, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyCollection<ProviderRegistration> GetAll() =>
        _providers.Values
            .OrderBy(registration => registration.ProviderId.Value, StringComparer.Ordinal)
            .ToArray();

    public bool Remove(ProviderId providerId) => _providers.TryRemove(providerId, out _);

    public void Clear() => _providers.Clear();
}
