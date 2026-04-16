namespace PiSharp.Mom;

public sealed class MomSlackWorkspaceIndex
{
    private readonly Dictionary<string, SlackUserInfo> _usersById;
    private readonly Dictionary<string, SlackChannelInfo> _channelsById;

    public MomSlackWorkspaceIndex(
        IEnumerable<SlackUserInfo>? users = null,
        IEnumerable<SlackChannelInfo>? channels = null)
    {
        Users = (users ?? Array.Empty<SlackUserInfo>())
            .OrderBy(static user => user.UserName, StringComparer.Ordinal)
            .ToArray();
        Channels = (channels ?? Array.Empty<SlackChannelInfo>())
            .OrderBy(static channel => channel.Name, StringComparer.Ordinal)
            .ToArray();

        _usersById = Users.ToDictionary(static user => user.Id, StringComparer.Ordinal);
        _channelsById = Channels.ToDictionary(static channel => channel.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<SlackUserInfo> Users { get; }

    public IReadOnlyList<SlackChannelInfo> Channels { get; }

    public SlackUserInfo? FindUser(string userId) =>
        string.IsNullOrWhiteSpace(userId)
            ? null
            : _usersById.GetValueOrDefault(userId);

    public SlackChannelInfo? FindChannel(string channelId) =>
        string.IsNullOrWhiteSpace(channelId)
            ? null
            : _channelsById.GetValueOrDefault(channelId);
}
