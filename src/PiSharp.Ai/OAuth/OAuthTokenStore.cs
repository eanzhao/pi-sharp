using System.Text.Json;

namespace PiSharp.Ai;

public sealed class OAuthTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly IReadOnlyDictionary<ProviderId, OAuthSettings> _oauthSettings;
    private readonly OAuthClient _oauthClient;
    private readonly TimeProvider _timeProvider;

    public OAuthTokenStore(
        IEnumerable<ProviderConfiguration> providers,
        OAuthClient? oauthClient = null,
        string? tokenDirectory = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _oauthSettings = providers
            .Where(static provider => provider.OAuthSettings is not null)
            .ToDictionary(static provider => provider.ProviderId, static provider => provider.OAuthSettings!);
        _oauthClient = oauthClient ?? new OAuthClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
        TokenDirectory = Path.GetFullPath(tokenDirectory ?? GetDefaultTokenDirectory());
    }

    public string TokenDirectory { get; }

    public async Task SaveTokenAsync(
        ProviderId providerId,
        OAuthTokenResponse tokenResponse,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenResponse);

        var storedToken = new StoredToken
        {
            SavedAtUtc = _timeProvider.GetUtcNow(),
            Token = tokenResponse,
        };

        Directory.CreateDirectory(TokenDirectory);
        await File.WriteAllTextAsync(
                GetTokenPath(providerId),
                JsonSerializer.Serialize(storedToken, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OAuthTokenResponse?> LoadTokenAsync(
        ProviderId providerId,
        CancellationToken cancellationToken = default)
    {
        var tokenPath = GetTokenPath(providerId);
        if (!File.Exists(tokenPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(tokenPath, cancellationToken).ConfigureAwait(false);
        var storedToken = JsonSerializer.Deserialize<StoredToken>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize OAuth token from '{tokenPath}'.");

        if (!IsExpired(storedToken))
        {
            return storedToken.Token;
        }

        if (string.IsNullOrWhiteSpace(storedToken.Token.RefreshToken) ||
            !_oauthSettings.TryGetValue(providerId, out var oauthSettings))
        {
            return null;
        }

        var refreshedToken = await _oauthClient
            .RefreshTokenAsync(
                oauthSettings.TokenEndpoint,
                oauthSettings.ClientId,
                storedToken.Token.RefreshToken,
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(refreshedToken.RefreshToken))
        {
            refreshedToken = refreshedToken with
            {
                RefreshToken = storedToken.Token.RefreshToken,
            };
        }

        await SaveTokenAsync(providerId, refreshedToken, cancellationToken).ConfigureAwait(false);
        return refreshedToken;
    }

    private bool IsExpired(StoredToken token)
    {
        if (token.Token.ExpiresIn is null)
        {
            return false;
        }

        if (token.Token.ExpiresIn <= 0)
        {
            return true;
        }

        return token.SavedAtUtc + TimeSpan.FromSeconds(token.Token.ExpiresIn.Value) <= _timeProvider.GetUtcNow();
    }

    private string GetTokenPath(ProviderId providerId) =>
        Path.Combine(TokenDirectory, $"{providerId.Value.ToLowerInvariant()}.json");

    private static string GetDefaultTokenDirectory()
    {
        var home =
            Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrWhiteSpace(home))
        {
            home = Directory.GetCurrentDirectory();
        }

        return Path.Combine(home, ".pi-sharp", "tokens");
    }

    private sealed record StoredToken
    {
        public required DateTimeOffset SavedAtUtc { get; init; }

        public required OAuthTokenResponse Token { get; init; }
    }
}
