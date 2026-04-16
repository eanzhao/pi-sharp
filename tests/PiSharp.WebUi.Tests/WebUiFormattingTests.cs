using PiSharp.Ai;
using PiSharp.Agent;

namespace PiSharp.WebUi.Tests;

public sealed class WebUiFormattingTests
{
    [Fact]
    public void FormatUsage_IncludesTokenCountsAndTotalCost()
    {
        var usage = new ExtendedUsageDetails
        {
            InputTokenCount = 1_200,
            OutputTokenCount = 240,
            Cost = new UsageCostBreakdown(0.001m, 0.002m, 0m, 0m),
        };

        var formatted = PiSharp.WebUi.WebUiFormatting.FormatUsage(usage);

        Assert.Equal("↑1.2k ↓240 $0.0030", formatted);
    }

    [Fact]
    public void GetArtifacts_ReadsArtifactPayloadsFromToolResults()
    {
        var toolCall = new Microsoft.Extensions.AI.FunctionCallContent(
            "call-1",
            "artifacts",
            new Dictionary<string, object?>());

        var message = AgentMessageMetadata.CreateToolResultMessage(
            toolCall,
            AgentToolResult.FromValue(
                new
                {
                    artifacts = new[]
                    {
                        new
                        {
                            id = "demo.html",
                            version = 3,
                            contentType = "html",
                            content = "<h1>Hello</h1>",
                        },
                    },
                }),
            isError: false);

        var artifacts = PiSharp.WebUi.WebUiFormatting.GetArtifacts(message);

        var artifact = Assert.Single(artifacts);
        Assert.Equal("demo.html", artifact.ArtifactId);
        Assert.Equal(3, artifact.VersionNumber);
        Assert.Equal("html", artifact.ContentType);
    }
}
