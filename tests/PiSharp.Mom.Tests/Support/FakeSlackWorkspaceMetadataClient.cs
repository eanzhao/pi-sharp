using PiSharp.Mom;

namespace PiSharp.Mom.Tests.Support;

internal sealed class FakeSlackWorkspaceMetadataClient : ISlackWorkspaceMetadataClient
{
    private readonly Queue<WorkspaceSnapshot> _snapshots = new();
    private WorkspaceSnapshot _activeSnapshot = WorkspaceSnapshot.Empty;
    private WorkspaceSnapshot _lastSnapshot = WorkspaceSnapshot.Empty;

    public int GetUsersCallCount { get; private set; }

    public int GetChannelsCallCount { get; private set; }

    public void EnqueueSnapshot(
        IReadOnlyList<SlackUserInfo>? users = null,
        IReadOnlyList<SlackChannelInfo>? channels = null)
    {
        _snapshots.Enqueue(new WorkspaceSnapshot(
            users ?? Array.Empty<SlackUserInfo>(),
            channels ?? Array.Empty<SlackChannelInfo>()));
    }

    public Task<IReadOnlyList<SlackUserInfo>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        GetUsersCallCount++;
        _activeSnapshot = _snapshots.Count > 0 ? _snapshots.Dequeue() : _lastSnapshot;
        _lastSnapshot = _activeSnapshot;
        return Task.FromResult(_activeSnapshot.Users);
    }

    public Task<IReadOnlyList<SlackChannelInfo>> GetChannelsAsync(
        IReadOnlyList<SlackUserInfo> users,
        CancellationToken cancellationToken = default)
    {
        GetChannelsCallCount++;
        return Task.FromResult(_activeSnapshot.Channels);
    }

    private sealed record WorkspaceSnapshot(
        IReadOnlyList<SlackUserInfo> Users,
        IReadOnlyList<SlackChannelInfo> Channels)
    {
        public static WorkspaceSnapshot Empty { get; } =
            new(Array.Empty<SlackUserInfo>(), Array.Empty<SlackChannelInfo>());
    }
}
