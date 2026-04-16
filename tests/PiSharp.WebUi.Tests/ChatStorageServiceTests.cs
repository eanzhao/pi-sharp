using PiSharp.CodingAgent;
using PiSharp.WebUi.Tests.Support;

namespace PiSharp.WebUi.Tests;

public sealed class ChatStorageServiceTests
{
    [Fact]
    public async Task SaveLoadListAndDeleteSessionAsync_UsesJsStorageModule()
    {
        var store = new Dictionary<string, string>(StringComparer.Ordinal);
        var module = new FakeJsModule();
        module.Register("openDb", _ => null);
        module.Register("saveSession", args =>
        {
            store[(string)args![0]!] = (string)args[1]!;
            return null;
        });
        module.Register("loadSession", args =>
            store.TryGetValue((string)args![0]!, out var payload) ? payload : null);
        module.Register("listSessions", _ => store.Keys.OrderBy(static key => key, StringComparer.Ordinal).ToArray());
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

        await storage.SaveSessionAsync("session-1", messages);

        var loaded = await storage.LoadSessionAsync("session-1");
        var sessions = await storage.ListSessionsAsync();

        Assert.Single(loaded);
        Assert.Equal("assistant", loaded[0].Role);
        Assert.Equal("Saved reply", loaded[0].Contents[0].Text);
        Assert.Equal(["session-1"], sessions);

        await storage.DeleteSessionAsync("session-1");

        Assert.Empty(await storage.ListSessionsAsync());
    }
}
