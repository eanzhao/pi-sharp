using System.Text.Json;
using PiSharp.Mom;

namespace PiSharp.Mom.Tests;

public sealed class SlackSocketModeClientTests
{
    [Fact]
    public void TryParseIncomingEvent_ParsesMentionEvents()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "events_api",
              "payload": {
                "type": "event_callback",
                "event": {
                  "type": "app_mention",
                  "user": "U123",
                  "channel": "C123",
                  "text": "<@B999> summarize this",
                  "ts": "12345.6789"
                }
              }
            }
            """);

        var parsed = SlackSocketModeClient.TryParseIncomingEvent(document.RootElement, "B999", out var incomingEvent);

        Assert.True(parsed);
        Assert.NotNull(incomingEvent);
        Assert.Equal("C123", incomingEvent!.ChannelId);
        Assert.Equal("U123", incomingEvent.UserId);
        Assert.False(incomingEvent.IsDirectMessage);
    }

    [Fact]
    public void TryParseIncomingEvent_ParsesDirectMessages()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "events_api",
              "payload": {
                "type": "event_callback",
                "event": {
                  "type": "message",
                  "user": "U123",
                  "channel": "D123",
                  "text": "hello",
                  "ts": "12345.6789"
                }
              }
            }
            """);

        var parsed = SlackSocketModeClient.TryParseIncomingEvent(document.RootElement, "B999", out var incomingEvent);

        Assert.True(parsed);
        Assert.NotNull(incomingEvent);
        Assert.True(incomingEvent!.IsDirectMessage);
    }

    [Fact]
    public void TryParseIncomingEvent_ParsesChannelChatterAsLogOnly()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "events_api",
              "payload": {
                "type": "event_callback",
                "event": {
                  "type": "message",
                  "user": "U123",
                  "channel": "C123",
                  "text": "general chatter",
                  "ts": "12345.6789"
                }
              }
            }
            """);

        var parsed = SlackSocketModeClient.TryParseIncomingEvent(document.RootElement, "B999", out var incomingEvent);

        Assert.True(parsed);
        Assert.NotNull(incomingEvent);
        Assert.False(incomingEvent!.RequiresResponse);
        Assert.True(incomingEvent.ShouldLogToChannelLog);
    }

    [Fact]
    public void TryParseIncomingEvent_ParsesFileShareMessages()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "events_api",
              "payload": {
                "type": "event_callback",
                "event": {
                  "type": "message",
                  "subtype": "file_share",
                  "user": "U123",
                  "channel": "C123",
                  "ts": "12345.6789",
                  "files": [
                    {
                      "name": "notes.txt",
                      "url_private_download": "https://example.com/notes.txt"
                    }
                  ]
                }
              }
            }
            """);

        var parsed = SlackSocketModeClient.TryParseIncomingEvent(document.RootElement, "B999", out var incomingEvent);

        Assert.True(parsed);
        Assert.NotNull(incomingEvent);
        Assert.False(incomingEvent!.RequiresResponse);
        var file = Assert.Single(incomingEvent.Files!);
        Assert.Equal("notes.txt", file.Name);
        Assert.Equal("https://example.com/notes.txt", file.PrivateDownloadUrl);
    }

    [Fact]
    public void TryParseIncomingEvent_IgnoresMessagesFromBotUser()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "events_api",
              "payload": {
                "type": "event_callback",
                "event": {
                  "type": "app_mention",
                  "user": "B999",
                  "channel": "C123",
                  "text": "<@B999> summarize this",
                  "ts": "12345.6789"
                }
              }
            }
            """);

        var parsed = SlackSocketModeClient.TryParseIncomingEvent(document.RootElement, "B999", out var incomingEvent);

        Assert.False(parsed);
        Assert.Null(incomingEvent);
    }
}
