using System.Threading;

namespace PiSharp.Mom;

public sealed class MomSlackWorkspaceIndex
{
    private WorkspaceSnapshot _snapshot;

    public MomSlackWorkspaceIndex(
        IEnumerable<SlackUserInfo>? users = null,
        IEnumerable<SlackChannelInfo>? channels = null)
    {
        _snapshot = WorkspaceSnapshot.Create(users, channels);
    }

    public IReadOnlyList<SlackUserInfo> Users => Volatile.Read(ref _snapshot).Users;

    public IReadOnlyList<SlackChannelInfo> Channels => Volatile.Read(ref _snapshot).Channels;

    public void Update(
        IEnumerable<SlackUserInfo>? users,
        IEnumerable<SlackChannelInfo>? channels)
    {
        Volatile.Write(ref _snapshot, WorkspaceSnapshot.Create(users, channels));
    }

    public SlackUserInfo? FindUser(string userId) =>
        string.IsNullOrWhiteSpace(userId)
            ? null
            : Volatile.Read(ref _snapshot).UsersById.GetValueOrDefault(userId);

    public SlackChannelInfo? FindChannel(string channelId) =>
        string.IsNullOrWhiteSpace(channelId)
            ? null
            : Volatile.Read(ref _snapshot).ChannelsById.GetValueOrDefault(channelId);

    private sealed record WorkspaceSnapshot(
        IReadOnlyList<SlackUserInfo> Users,
        IReadOnlyList<SlackChannelInfo> Channels,
        IReadOnlyDictionary<string, SlackUserInfo> UsersById,
        IReadOnlyDictionary<string, SlackChannelInfo> ChannelsById)
    {
        public static WorkspaceSnapshot Create(
            IEnumerable<SlackUserInfo>? users,
            IEnumerable<SlackChannelInfo>? channels)
        {
            var orderedUsers = (users ?? Array.Empty<SlackUserInfo>())
                .OrderBy(static user => user.UserName, StringComparer.Ordinal)
                .ToArray();
            var orderedChannels = (channels ?? Array.Empty<SlackChannelInfo>())
                .OrderBy(static channel => channel.Name, StringComparer.Ordinal)
                .ToArray();

            return new WorkspaceSnapshot(
                orderedUsers,
                orderedChannels,
                orderedUsers.ToDictionary(static user => user.Id, StringComparer.Ordinal),
                orderedChannels.ToDictionary(static channel => channel.Id, StringComparer.Ordinal));
        }
    }
}
