using System.Net;
using Microsoft.Extensions.AI;
using PiSharp.Ai;

namespace PiSharp.Ai.Tests;

public sealed class OAuthTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-oauth-{Guid.NewGuid():N}");

    [Fact]
    public void GenerateCodeVerifier_ReturnsPkceCompatibleValue()
    {
        var verifier = OAuthClient.GenerateCodeVerifier();

        Assert.InRange(verifier.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", verifier);
    }

    [Fact]
    public async Task LoadTokenAsync_SavesLoadsAndRefreshesExpiredTokens()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero));
        var refreshRequestCount = 0;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            refreshRequestCount++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://auth.example.com/token", request.RequestUri?.ToString());

            var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Assert.Contains("grant_type=refresh_token", body, StringComparison.Ordinal);
            Assert.Contains("client_id=client-id", body, StringComparison.Ordinal);
            Assert.Contains("refresh_token=refresh-token", body, StringComparison.Ordinal);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"fresh-token","expires_in":3600,"token_type":"Bearer"}"""),
            };
        });

        using var oauthClient = new OAuthClient(new HttpClient(handler));
        var store = new OAuthTokenStore(
            [
                new ProviderConfiguration(
                    ProviderId.OpenAi,
                    ApiId.OpenAi,
                    "OpenAI",
                    OAuthSettings: new OAuthSettings(
                        new Uri("https://auth.example.com/authorize"),
                        new Uri("https://auth.example.com/token"),
                        "client-id",
                        new Uri("http://127.0.0.1:8787/callback"),
                        ["openid", "profile"])),
            ],
            oauthClient,
            Path.Combine(_tempDirectory, "tokens"),
            timeProvider);

        await store.SaveTokenAsync(
            ProviderId.OpenAi,
            new OAuthTokenResponse
            {
                AccessToken = "initial-token",
                RefreshToken = "refresh-token",
                ExpiresIn = 60,
                TokenType = "Bearer",
            });

        var initial = await store.LoadTokenAsync(ProviderId.OpenAi);
        Assert.NotNull(initial);
        Assert.Equal("initial-token", initial!.AccessToken);
        Assert.Equal(0, refreshRequestCount);

        timeProvider.Advance(TimeSpan.FromSeconds(61));

        var refreshed = await store.LoadTokenAsync(ProviderId.OpenAi);
        Assert.NotNull(refreshed);
        Assert.Equal("fresh-token", refreshed!.AccessToken);
        Assert.Equal("refresh-token", refreshed.RefreshToken);
        Assert.Equal(1, refreshRequestCount);

        var persisted = await store.LoadTokenAsync(ProviderId.OpenAi);
        Assert.NotNull(persisted);
        Assert.Equal("fresh-token", persisted!.AccessToken);
        Assert.Equal(1, refreshRequestCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
