using Microsoft.Extensions.AI;
using PiSharp.Agent;

namespace PiSharp.WebUi.Tests;

public sealed class ArtifactRenderingTests
{
    [Fact]
    public async Task RenderAsync_ToolArtifactResultsUseArtifactPanel()
    {
        var toolCall = new FunctionCallContent(
            "call-1",
            "artifacts",
            new Dictionary<string, object?>
            {
                ["filename"] = "preview.html",
            });

        var message = AgentMessageMetadata.CreateToolResultMessage(
            toolCall,
            AgentToolResult.FromValue(new ArtifactVersion("preview.html", 1, "html", "<h1>Hello artifact</h1>")),
            isError: false);

        var html = await Support.ComponentRenderer.RenderAsync<MessageList>(
            new Dictionary<string, object?>
            {
                ["Messages"] = new[] { message },
            });

        Assert.Contains("sandbox=\"allow-scripts\"", html, StringComparison.Ordinal);
        Assert.Contains("preview.html", html, StringComparison.Ordinal);
        Assert.Contains("tool result", html, StringComparison.OrdinalIgnoreCase);
    }
}
