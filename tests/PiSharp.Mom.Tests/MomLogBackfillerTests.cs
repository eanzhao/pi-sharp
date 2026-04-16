using System.Net;
using System.Text;
using PiSharp.Mom;

namespace PiSharp.Mom.Tests;

public sealed class MomLogBackfillerTests : IDisposable
{
    private readonly string _workspaceDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-mom-backfill-{Guid.NewGuid():N}");

    [Fact]
    public async Task BackfillAllAsync_LogsNewMessagesAndAttachmentsForExistingChannels()
    {
        Directory.CreateDirectory(Path.Combine(_workspaceDirectory, "C123"));
        await File.WriteAllTextAsync(
            Path.Combine(_workspaceDirectory, "C123", "log.jsonl"),
            """
            {"date":"2026-04-16T00:00:00.0000000+00:00","ts":"100.000000","user":"U111","text":"existing","attachments":[],"isBot":false}
            """);

        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/conversations.history", StringComparison.Ordinal) == true)
            {
                var response =
                    """
                    {
                      "ok": true,
                      "messages": [
                        {
                          "user": "U222",
                          "text": "<@B123> new question",
                          "ts": "101.000000"
                        },
                        {
                          "user": "B123",
                          "text": "previous answer",
                          "ts": "102.000000"
                        },
                        {
                          "bot_id": "BOTX",
                          "text": "other bot",
                          "ts": "103.000000"
                        },
                        {
                          "user": "U333",
                          "subtype": "file_share",
                          "text": "",
                          "ts": "104.000000",
                          "files": [
                            {
                              "name": "notes.txt",
                              "url_private_download": "https://files.example.com/notes.txt"
                            }
                          ]
                        }
                      ],
                      "response_metadata": {
                        "next_cursor": ""
                      }
                    }
                    """;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response, Encoding.UTF8, "application/json"),
                };
            }

            if (request.RequestUri?.Host == "files.example.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("attachment body", Encoding.UTF8, "text/plain"),
                };
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }));

        using var slackClient = new SlackWebApiClient("xoxb-test", httpClient);
        using var store = new MomChannelStore(_workspaceDirectory, "xoxb-test", httpClient);
        var backfiller = new MomLogBackfiller(slackClient, store);

        var result = await backfiller.BackfillAllAsync("B123");

        Assert.Equal(1, result.ChannelsScanned);
        Assert.Equal(3, result.MessagesLogged);

        var logLines = File.ReadAllLines(Path.Combine(_workspaceDirectory, "C123", "log.jsonl"));
        Assert.Equal(4, logLines.Length);
        Assert.Contains("\"text\":\"new question\"", logLines[1]);
        Assert.Contains("\"user\":\"bot\"", logLines[2]);
        Assert.Contains("\"original\":\"notes.txt\"", logLines[3]);
        Assert.Contains("\"local\":\"attachments/104000_notes.txt\"", logLines[3]);

        var attachmentPath = Path.Combine(_workspaceDirectory, "C123", "attachments", "104000_notes.txt");
        Assert.True(File.Exists(attachmentPath));
        Assert.Equal("attachment body", await File.ReadAllTextAsync(attachmentPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceDirectory))
        {
            Directory.Delete(_workspaceDirectory, recursive: true);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> createResponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(createResponse(request));
    }
}
