using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PiSharp.Ai;

public sealed record OAuthAuthorizationResult(string Code, string CodeVerifier, string State);

public sealed class OAuthClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly Func<Uri, CancellationToken, Task> _openBrowserAsync;
    private readonly bool _disposeHttpClient;

    public OAuthClient(HttpClient? httpClient = null, Func<Uri, CancellationToken, Task>? openBrowserAsync = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _openBrowserAsync = openBrowserAsync ?? OpenBrowserAsync;
        _disposeHttpClient = httpClient is null;
    }

    public static string GenerateCodeVerifier(int byteCount = 32)
    {
        if (byteCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        var bytes = new byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    public async Task<OAuthAuthorizationResult> StartAuthorizationAsync(
        Uri authorizationEndpoint,
        string clientId,
        Uri redirectUri,
        IEnumerable<string> scopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizationEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentNullException.ThrowIfNull(scopes);

        ValidateRedirectUri(redirectUri);

        var codeVerifier = GenerateCodeVerifier();
        var state = GenerateState();
        var authorizationUri = BuildAuthorizationUri(
            authorizationEndpoint,
            clientId,
            redirectUri,
            scopes,
            state,
            CreateCodeChallenge(codeVerifier));

        using var listener = new HttpListener();
        listener.Prefixes.Add(GetListenerPrefix(redirectUri));
        listener.Start();

        using var registration = cancellationToken.Register(static state => ((HttpListener)state!).Stop(), listener);

        await _openBrowserAsync(authorizationUri, cancellationToken).ConfigureAwait(false);

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var error = context.Request.QueryString["error"];
        if (!string.IsNullOrWhiteSpace(error))
        {
            await WriteBrowserResponseAsync(context.Response, success: false).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"OAuth authorization failed: {error} {context.Request.QueryString["error_description"]}".Trim());
        }

        var returnedState = context.Request.QueryString["state"];
        if (!string.Equals(state, returnedState, StringComparison.Ordinal))
        {
            await WriteBrowserResponseAsync(context.Response, success: false).ConfigureAwait(false);
            throw new InvalidOperationException("OAuth authorization failed: state mismatch.");
        }

        var code = context.Request.QueryString["code"];
        if (string.IsNullOrWhiteSpace(code))
        {
            await WriteBrowserResponseAsync(context.Response, success: false).ConfigureAwait(false);
            throw new InvalidOperationException("OAuth authorization failed: authorization code was not returned.");
        }

        await WriteBrowserResponseAsync(context.Response, success: true).ConfigureAwait(false);
        return new OAuthAuthorizationResult(code, codeVerifier, state);
    }

    public Task<OAuthTokenResponse> ExchangeCodeAsync(
        Uri tokenEndpoint,
        string clientId,
        string code,
        string codeVerifier,
        Uri redirectUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        ArgumentNullException.ThrowIfNull(redirectUri);

        return PostTokenRequestAsync(
            tokenEndpoint,
            [
                new("grant_type", "authorization_code"),
                new("client_id", clientId),
                new("code", code),
                new("code_verifier", codeVerifier),
                new("redirect_uri", redirectUri.ToString()),
            ],
            cancellationToken);
    }

    public Task<OAuthTokenResponse> RefreshTokenAsync(
        Uri tokenEndpoint,
        string clientId,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        return PostTokenRequestAsync(
            tokenEndpoint,
            [
                new("grant_type", "refresh_token"),
                new("client_id", clientId),
                new("refresh_token", refreshToken),
            ],
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<OAuthTokenResponse> PostTokenRequestAsync(
        Uri tokenEndpoint,
        IEnumerable<KeyValuePair<string, string>> payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(payload),
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var token = JsonSerializer.Deserialize<OAuthTokenResponse>(json, JsonOptions)
            ?? throw new InvalidOperationException("OAuth token response was empty.");

        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("OAuth token response did not contain an access token.");
        }

        return token;
    }

    private static void ValidateRedirectUri(Uri redirectUri)
    {
        if (!redirectUri.IsAbsoluteUri)
        {
            throw new ArgumentException("Redirect URI must be absolute.", nameof(redirectUri));
        }

        if (!string.Equals(redirectUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Redirect URI must use http for the local listener.", nameof(redirectUri));
        }

        if (!redirectUri.IsLoopback)
        {
            throw new ArgumentException("Redirect URI must use a loopback host for the local listener.", nameof(redirectUri));
        }
    }

    private static Uri BuildAuthorizationUri(
        Uri authorizationEndpoint,
        string clientId,
        Uri redirectUri,
        IEnumerable<string> scopes,
        string state,
        string codeChallenge)
    {
        var scope = string.Join(
            ' ',
            scopes.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim()));

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", clientId),
            new("redirect_uri", redirectUri.ToString()),
            new("state", state),
            new("code_challenge", codeChallenge),
            new("code_challenge_method", "S256"),
        };

        if (!string.IsNullOrWhiteSpace(scope))
        {
            parameters.Add(new("scope", scope));
        }

        var builder = new UriBuilder(authorizationEndpoint);
        var query = string.Join(
            "&",
            parameters.Select(static parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));

        builder.Query = string.IsNullOrWhiteSpace(builder.Query)
            ? query
            : $"{builder.Query.TrimStart('?')}&{query}";

        return builder.Uri;
    }

    private static string GetListenerPrefix(Uri redirectUri)
    {
        var prefix = redirectUri.GetLeftPart(UriPartial.Path);
        return prefix.EndsWith("/", StringComparison.Ordinal)
            ? prefix
            : $"{prefix}/";
    }

    private static async Task WriteBrowserResponseAsync(HttpListenerResponse response, bool success)
    {
        var html = success
            ? "<html><body><h1>PiSharp authorization complete</h1><p>You can close this window.</p></body></html>"
            : "<html><body><h1>PiSharp authorization failed</h1><p>You can close this window and return to the CLI.</p></body></html>";

        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = success ? (int)HttpStatusCode.OK : (int)HttpStatusCode.BadRequest;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;

        await using var output = response.OutputStream;
        await output.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
    }

    private static Task OpenBrowserAsync(Uri authorizationUri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var process = Process.Start(
                new ProcessStartInfo
                {
                    FileName = authorizationUri.ToString(),
                    UseShellExecute = true,
                });

            if (process is null)
            {
                throw new InvalidOperationException("The browser process did not start.");
            }

            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Failed to open a browser for OAuth authorization. Open this URL manually: {authorizationUri}",
                exception);
        }
    }

    private static string GenerateState() => GenerateCodeVerifier();

    private static string CreateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
