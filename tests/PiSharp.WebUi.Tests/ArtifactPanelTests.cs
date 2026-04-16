using System.Net;

namespace PiSharp.WebUi.Tests;

public sealed class ArtifactPanelTests
{
    [Fact]
    public async Task RenderAsync_UsesSandboxedIframeForMarkdownArtifacts()
    {
        var html = await Support.ComponentRenderer.RenderAsync<ArtifactPanel>(
            new Dictionary<string, object?>
            {
                ["Artifact"] = new ArtifactVersion("preview", 2, "markdown", "# Hello Artifact"),
            });

        var decoded = WebUtility.HtmlDecode(html);

        Assert.Contains("sandbox=\"allow-scripts\"", html, StringComparison.Ordinal);
        Assert.Contains("preview", html, StringComparison.Ordinal);
        Assert.Contains("v2", html, StringComparison.Ordinal);
        Assert.Contains("srcdoc=", html, StringComparison.Ordinal);
        Assert.Contains("Hello Artifact", decoded, StringComparison.Ordinal);
    }
}
