using Microsoft.Extensions.AI;
using PiSharp.Mom;
using PiSharp.Mom.Tests.Support;

namespace PiSharp.Mom.Tests;

public sealed class MomSlackToolsTests : IDisposable
{
    private readonly string _workspaceDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-mom-tools-{Guid.NewGuid():N}");

    [Fact]
    public async Task AttachTool_UploadsWorkspaceFileToSlack()
    {
        var channelDirectory = Path.Combine(_workspaceDirectory, "C123");
        Directory.CreateDirectory(Path.Combine(channelDirectory, "scratch"));
        var reportPath = Path.Combine(channelDirectory, "scratch", "report.txt");
        await File.WriteAllTextAsync(reportPath, "report body");

        var slackClient = new FakeSlackMessagingClient();
        var tool = MomSlackTools.CreateAttachTool(_workspaceDirectory, channelDirectory, "C123", slackClient);

        var result = await tool.ExecuteAsync(
            "call-1",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["label"] = "Daily summary",
                    ["path"] = "scratch/report.txt",
                }));

        var upload = Assert.Single(slackClient.Uploads);
        Assert.Equal("C123", upload.ChannelId);
        Assert.Equal(reportPath, upload.FilePath);
        Assert.Equal("report.txt", upload.Title);
        var content = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Contains("Attached file: report.txt (Daily summary)", content.Text, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceDirectory))
        {
            Directory.Delete(_workspaceDirectory, recursive: true);
        }
    }
}
