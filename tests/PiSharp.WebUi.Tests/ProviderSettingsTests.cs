using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PiSharp.WebUi.Tests.Support;

namespace PiSharp.WebUi.Tests;

public sealed class ProviderSettingsTests
{
    [Fact]
    public async Task RenderAsync_LoadsSavedProvidersFromLocalStorage()
    {
        var jsRuntime = new FakeJsRuntime();
        jsRuntime.LocalStorage["pi-sharp.webui.custom-providers"] = JsonSerializer.Serialize(new[]
        {
            new CustomProviderConfig(
                "http://localhost:1234/v1",
                "key",
                "qwen2.5-coder",
                "LM Studio"),
        });

        var html = await ComponentRenderer.RenderAsync<ProviderSettings>(
            configureServices: services => services.AddSingleton<IJSRuntime>(jsRuntime));

        Assert.Contains("LM Studio", html, StringComparison.Ordinal);
        Assert.Contains("http://localhost:1234/v1", html, StringComparison.Ordinal);
        Assert.Contains("qwen2.5-coder", html, StringComparison.Ordinal);
    }
}
