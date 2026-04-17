using PiSharp.CodingAgent;
using PiSharp.WebUi.Tests.Support;
using System.Text.Json;

namespace PiSharp.WebUi.Tests;

public sealed class ChatStorageServiceTests
{
    [Fact]
    public async Task SaveLoadListAndDeleteSessionAsync_UsesJsStorageModule()
    {
        var store = new Dictionary<string, StoredSession>(StringComparer.Ordinal);
        var module = new FakeJsModule();
        var webJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        module.Register("openDb", _ => null);
        module.Register("saveSession", args =>
        {
            var sessionId = (string)args![0]!;
            store[sessionId] = new StoredSession(
                sessionId,
                (string)args[1]!,
                JsonSerializer.Deserialize<ChatSessionMetadata>((string)args[2]!, webJson)!);
            return null;
        });
        module.Register("loadSessionRecord", args =>
        {
            if (!store.TryGetValue((string)args![0]!, out var session))
            {
                return null;
            }

            return JsonSerializer.Serialize(new ChatSessionRecord(
                session.Metadata,
                JsonSerializer.Deserialize<SessionChatMessage[]>(session.Payload, webJson) ?? Array.Empty<SessionChatMessage>()), webJson);
        });
        module.Register("getSessionMetadata", args =>
            store.TryGetValue((string)args![0]!, out var session)
                ? JsonSerializer.Serialize(session.Metadata, webJson)
                : null);
        module.Register("listSessions", _ =>
            store.Values
                .OrderByDescending(static session => session.Metadata.UpdatedAt)
                .Select(static session => session.SessionId)
                .ToArray());
        module.Register("searchSessions", args =>
        {
            var query = JsonSerializer.Deserialize<ChatSessionQuery>((string)args![0]!, webJson) ?? new ChatSessionQuery();
            var results = store.Values
                .Select(static session => session.Metadata)
                .Where(metadata =>
                    string.IsNullOrWhiteSpace(query.TitleContains) ||
                    metadata.Title.Contains(query.TitleContains, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static metadata => metadata.UpdatedAt)
                .ToArray();

            return JsonSerializer.Serialize(results, webJson);
        });
        module.Register("deleteSession", args =>
        {
            store.Remove((string)args![0]!);
            return null;
        });

        var jsRuntime = new FakeJsRuntime();
        jsRuntime.RegisterModule("./_content/PiSharp.WebUi/pisharp-storage.js", module);

        await using var storage = new ChatStorageService(jsRuntime);
        Assert.IsAssignableFrom<IChatStorageService>(storage);

        SessionChatMessage[] messages =
        [
            new SessionChatMessage
            {
                Role = "assistant",
                Contents =
                [
                    new SessionContent
                    {
                        Type = SessionContentType.Text,
                        Text = "Saved reply",
                    },
                ],
            },
        ];

        var metadata = new ChatSessionMetadata(
            "session-1",
            "Saved reply",
            "gpt-4.1-mini",
            new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 16, 0, 1, 0, TimeSpan.Zero));

        await storage.SaveSessionAsync("session-1", metadata, messages);

        var loaded = await storage.LoadSessionAsync("session-1");
        var sessions = await storage.ListSessionsAsync();
        var listedMetadata = await storage.ListSessionsAsync(new ChatSessionQuery("Saved"));
        var loadedMetadata = await storage.GetSessionMetadataAsync("session-1");

        Assert.Single(loaded);
        Assert.Equal("assistant", loaded[0].Role);
        Assert.Equal("Saved reply", loaded[0].Contents[0].Text);
        Assert.Equal(["session-1"], sessions);
        Assert.Equal("Saved reply", Assert.Single(listedMetadata).Title);
        Assert.Equal("gpt-4.1-mini", loadedMetadata?.ModelId);

        await storage.DeleteSessionAsync("session-1");

        Assert.Empty(await storage.ListSessionsAsync());
    }

    [Fact]
    public async Task InMemoryStorage_SearchesByTitleAndLoadsMetadata()
    {
        var storage = new InMemoryChatStorageService();
        SessionChatMessage[] messages =
        [
            new SessionChatMessage
            {
                Role = "user",
                Contents =
                [
                    new SessionContent
                    {
                        Type = SessionContentType.Text,
                        Text = "Investigate retry behavior",
                    },
                ],
            },
        ];

        await storage.SaveSessionAsync(
            "session-1",
            new ChatSessionMetadata(
                "session-1",
                "Retry diagnostics",
                "gpt-4.1-mini",
                new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero)),
            messages);
        await storage.SaveSessionAsync(
            "session-2",
            new ChatSessionMetadata(
                "session-2",
                "Model catalog refresh",
                "claude-3-7-sonnet-latest",
                new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero)),
            messages);

        var matches = await storage.ListSessionsAsync(new ChatSessionQuery("retry"));
        var record = await storage.LoadSessionRecordAsync("session-1");

        Assert.Equal("session-1", Assert.Single(matches).SessionId);
        Assert.Equal("Retry diagnostics", record?.Metadata.Title);
        Assert.Equal("gpt-4.1-mini", record?.Metadata.ModelId);
        Assert.Single(record?.Messages ?? Array.Empty<SessionChatMessage>());
    }

    private sealed record StoredSession(
        string SessionId,
        string Payload,
        ChatSessionMetadata Metadata);
}
