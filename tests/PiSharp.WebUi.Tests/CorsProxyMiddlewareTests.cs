using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace PiSharp.WebUi.Tests;

public sealed class CorsProxyMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ProxiesMatchingRequestsAndForwardsHeaders()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("proxied"),
            Headers =
            {
                { "X-Upstream", "yes" },
            },
        });
        var middleware = CreateMiddleware(
            handler,
            _ => Task.CompletedTask,
            new Uri("https://upstream.example.com/base"));
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/proxy/v1/chat";
        context.Request.QueryString = new QueryString("?mode=fast");
        context.Request.Headers["Authorization"] = "Bearer test-key";
        context.Request.Headers["X-Test"] = "one";
        context.Request.Body = new MemoryStream("payload"u8.ToArray());
        context.Request.ContentLength = context.Request.Body.Length;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://upstream.example.com/base/v1/chat?mode=fast", handler.LastRequest!.RequestUri?.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.Equal("one", Assert.Single(handler.LastRequest.Headers.GetValues("X-Test")));
        Assert.Equal(StatusCodes.Status202Accepted, context.Response.StatusCode);
        Assert.Equal("yes", context.Response.Headers["X-Upstream"].ToString());

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        Assert.Equal("proxied", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task InvokeAsync_CallsNextWhenPathDoesNotMatch()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            new RecordingHandler(_ => throw new InvalidOperationException("proxy should not be called")),
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new Uri("https://upstream.example.com"));
        var context = new DefaultHttpContext();
        context.Request.Path = "/health";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_Returns503WhenTargetIsMissing()
    {
        var middleware = new CorsProxyMiddleware(
            _ => Task.CompletedTask,
            new StaticHttpClientFactory(new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException("proxy should not be called")))),
            Options.Create(new PiSharpCorsProxyOptions()),
            new PathString("/api/proxy"));
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/proxy/test";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        Assert.Contains("not configured", await reader.ReadToEndAsync(), StringComparison.OrdinalIgnoreCase);
    }

    private static CorsProxyMiddleware CreateMiddleware(
        HttpMessageHandler handler,
        RequestDelegate next,
        Uri targetUrl) =>
        new(
            next,
            new StaticHttpClientFactory(new HttpClient(handler)),
            Options.Create(new PiSharpCorsProxyOptions
            {
                TargetUrl = targetUrl,
            }),
            new PathString("/api/proxy"));

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responseFactory(request));
        }
    }
}
