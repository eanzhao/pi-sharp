using PiSharp.CodingAgent;

namespace PiSharp.CodingAgent.Tests;

public sealed class SessionManagerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"pisharp_session_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void NewSession_CreatesJsonlFile()
    {
        var manager = new SessionManager(_tempDir, "/tmp");

        var sessionId = manager.NewSession();

        Assert.NotNull(sessionId);
        Assert.NotNull(manager.SessionFile);
        Assert.True(File.Exists(manager.SessionFile));
        Assert.NotNull(manager.Header);
        Assert.Equal(sessionId, manager.Header!.Id);
    }

    [Fact]
    public void AppendEntry_PersistsAndTracksLeaf()
    {
        var manager = new SessionManager(_tempDir, "/tmp");
        manager.NewSession();

        var entry = new SessionMessageEntry
        {
            Id = SessionEntry.NewId(),
            Timestamp = SessionEntry.Now(),
            Role = "user",
            Text = "hello",
        };

        manager.AppendEntry(entry);

        Assert.Equal(entry.Id, manager.LeafId);
        Assert.Single(manager.Entries);
        Assert.Same(entry, manager.GetEntry(entry.Id));
    }

    [Fact]
    public void AppendEntry_AutoSetsParentId()
    {
        var manager = new SessionManager(_tempDir, "/tmp");
        manager.NewSession();

        var first = new SessionMessageEntry
        {
            Id = SessionEntry.NewId(),
            Timestamp = SessionEntry.Now(),
            Role = "user",
            Text = "first",
        };
        manager.AppendEntry(first);

        var second = new SessionMessageEntry
        {
            Id = SessionEntry.NewId(),
            Timestamp = SessionEntry.Now(),
            Role = "assistant",
            Text = "second",
        };
        manager.AppendEntry(second);

        Assert.Equal(first.Id, manager.Entries[1].ParentId);
    }

    [Fact]
    public async Task LoadSession_RestoresEntriesFromFile()
    {
        var manager1 = new SessionManager(_tempDir, "/tmp");
        manager1.NewSession();
        manager1.AppendEntry(new SessionMessageEntry
        {
            Id = "e1",
            Timestamp = SessionEntry.Now(),
            Role = "user",
            Text = "hello",
        });
        manager1.AppendEntry(new SessionMessageEntry
        {
            Id = "e2",
            Timestamp = SessionEntry.Now(),
            Role = "assistant",
            Text = "hi there",
        });

        var manager2 = new SessionManager(_tempDir, "/tmp");
        await manager2.LoadSessionAsync(manager1.SessionFile!);

        Assert.Equal(2, manager2.Entries.Count);
        Assert.NotNull(manager2.Header);
        Assert.Equal("e2", manager2.LeafId);
    }

    [Fact]
    public void GetBranch_WalksFromLeafToRoot()
    {
        var manager = new SessionManager(_tempDir, "/tmp");
        manager.NewSession();

        manager.AppendEntry(new SessionMessageEntry { Id = "a", Timestamp = SessionEntry.Now(), Role = "user", Text = "1" });
        manager.AppendEntry(new SessionMessageEntry { Id = "b", Timestamp = SessionEntry.Now(), Role = "assistant", Text = "2" });
        manager.AppendEntry(new SessionMessageEntry { Id = "c", Timestamp = SessionEntry.Now(), Role = "user", Text = "3" });

        var branch = manager.GetBranch();

        Assert.Equal(3, branch.Count);
        Assert.Equal("a", branch[0].Id);
        Assert.Equal("b", branch[1].Id);
        Assert.Equal("c", branch[2].Id);
    }

    [Fact]
    public void SetLeaf_EnablesBranching()
    {
        var manager = new SessionManager(_tempDir, "/tmp");
        manager.NewSession();

        manager.AppendEntry(new SessionMessageEntry { Id = "a", Timestamp = SessionEntry.Now(), Role = "user", Text = "1" });
        manager.AppendEntry(new SessionMessageEntry { Id = "b", Timestamp = SessionEntry.Now(), Role = "assistant", Text = "2" });

        manager.SetLeaf("a");
        manager.AppendEntry(new SessionMessageEntry { Id = "c", Timestamp = SessionEntry.Now(), Role = "user", Text = "branch" });

        var branch = manager.GetBranch();

        Assert.Equal(2, branch.Count);
        Assert.Equal("a", branch[0].Id);
        Assert.Equal("c", branch[1].Id);
    }

    [Fact]
    public void BuildContext_CollectsMessagesAlongBranch()
    {
        var manager = new SessionManager(_tempDir, "/tmp");
        manager.NewSession();

        manager.AppendEntry(new SessionMessageEntry { Id = "a", Timestamp = SessionEntry.Now(), Role = "user", Text = "q1" });
        manager.AppendEntry(new SessionMessageEntry { Id = "b", Timestamp = SessionEntry.Now(), Role = "assistant", Text = "a1" });
        manager.AppendEntry(new ThinkingLevelChangeEntry { Id = "t", Timestamp = SessionEntry.Now(), Level = "high" });
        manager.AppendEntry(new ModelChangeEntry { Id = "m", Timestamp = SessionEntry.Now(), ProviderId = "anthropic", ModelId = "claude" });

        var ctx = manager.BuildContext();

        Assert.Equal(2, ctx.Messages.Count);
        Assert.Equal("q1", ctx.Messages[0].Text);
        Assert.Equal("a1", ctx.Messages[1].Text);
        Assert.Equal("high", ctx.ThinkingLevel);
        Assert.Equal("anthropic", ctx.ProviderId);
        Assert.Equal("claude", ctx.ModelId);
    }

    [Fact]
    public void BuildContext_HandlesCompactionBoundary()
    {
        var manager = new SessionManager(_tempDir, "/tmp");
        manager.NewSession();

        manager.AppendEntry(new SessionMessageEntry { Id = "old1", Timestamp = SessionEntry.Now(), Role = "user", Text = "old msg" });
        manager.AppendEntry(new CompactionEntry
        {
            Id = "comp",
            Timestamp = SessionEntry.Now(),
            Summary = "Previous: discussed old topic",
            FirstKeptEntryId = "new1",
            TokensBefore = 5000,
        });
        manager.AppendEntry(new SessionMessageEntry { Id = "new1", Timestamp = SessionEntry.Now(), Role = "user", Text = "new msg" });

        var ctx = manager.BuildContext();

        Assert.Equal(2, ctx.Messages.Count);
        Assert.Equal("Previous: discussed old topic", ctx.Messages[0].Text);
        Assert.Equal("new msg", ctx.Messages[1].Text);
    }

    [Fact]
    public void SetLeaf_ThrowsForUnknownEntry()
    {
        var manager = new SessionManager(_tempDir, "/tmp");
        manager.NewSession();

        Assert.Throws<KeyNotFoundException>(() => manager.SetLeaf("nonexistent"));
    }
}
