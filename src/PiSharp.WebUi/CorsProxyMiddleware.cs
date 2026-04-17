using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PiSharp.WebUi;

public sealed class PiSharpCorsProxyOptions
{
    public Uri? TargetUrl { get; set; }
}

public sealed class CorsProxyMiddleware
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Host",
    };

    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PiSharpCorsProxyOptions _options;
    private readonly PathString _pathPrefix;

    public CorsProxyMiddleware(
        RequestDelegate next,
        IHttpClientFactory httpClientFactory,
        IOptions<PiSharpCorsProxyOptions> options,
        PathString pathPrefix)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _pathPrefix = pathPrefix;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_pathPrefix.HasValue || !context.Request.Path.StartsWithSegments(_pathPrefix, out var remainingPath))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (_options.TargetUrl is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("PiSharp CORS proxy target URL is not configured.", context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        using var requestMessage = CreateProxyRequest(context, _options.TargetUrl, remainingPath);
        using var responseMessage = await _httpClientFactory
            .CreateClient(nameof(CorsProxyMiddleware))
            .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted)
            .ConfigureAwait(false);

        context.Response.StatusCode = (int)responseMessage.StatusCode;
        CopyHeaders(responseMessage.Headers, context.Response.Headers);
        if (responseMessage.Content is not null)
        {
            CopyHeaders(responseMessage.Content.Headers, context.Response.Headers);
            context.Response.Headers.Remove("transfer-encoding");
            await responseMessage.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static HttpRequestMessage CreateProxyRequest(HttpContext context, Uri targetUrl, PathString remainingPath)
    {
        var requestUri = BuildTargetUri(targetUrl, remainingPath, context.Request.QueryString);
        var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), requestUri);

        if (RequestCanHaveBody(context.Request))
        {
            requestMessage.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) &&
                requestMessage.Content is not null)
            {
                requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        return requestMessage;
    }

    private static Uri BuildTargetUri(Uri targetUrl, PathString remainingPath, QueryString queryString)
    {
        var builder = new UriBuilder(targetUrl);
        var basePath = string.IsNullOrWhiteSpace(builder.Path)
            ? string.Empty
            : builder.Path.TrimEnd('/');
        var suffix = remainingPath.HasValue ? remainingPath.Value : string.Empty;

        builder.Path = string.IsNullOrWhiteSpace(suffix)
            ? string.IsNullOrWhiteSpace(basePath) ? "/" : basePath
            : $"{basePath}{suffix}";
        builder.Query = queryString.HasValue ? queryString.Value![1..] : string.Empty;
        return builder.Uri;
    }

    private static bool RequestCanHaveBody(HttpRequest request) =>
        request.ContentLength is > 0 ||
        request.Headers.ContainsKey("Transfer-Encoding");

    private static void CopyHeaders(HttpHeaders headers, IHeaderDictionary destination)
    {
        foreach (var header in headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
            {
                continue;
            }

            destination[header.Key] = header.Value.ToArray();
        }
    }
}

public static class PiSharpCorsProxyExtensions
{
    public static IServiceCollection AddPiSharpCorsProxy(
        this IServiceCollection services,
        Action<PiSharpCorsProxyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(nameof(CorsProxyMiddleware));
        if (configure is not null)
        {
            services.Configure(configure);
        }

        return services;
    }

    public static IApplicationBuilder UsePiSharpCorsProxy(this IApplicationBuilder app, string pathPrefix)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathPrefix);

        return app.UseMiddleware<CorsProxyMiddleware>(new PathString(pathPrefix));
    }
}
