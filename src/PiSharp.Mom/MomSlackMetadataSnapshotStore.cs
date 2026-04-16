using System.Text.Json;

namespace PiSharp.Mom;

public sealed record MomSlackMetadataSnapshot(
    DateTimeOffset RefreshedAt,
    IReadOnlyList<SlackUserInfo> Users,
    IReadOnlyList<SlackChannelInfo> Channels);

public static class MomSlackMetadataSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static MomSlackMetadataSnapshot? Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<MomSlackMetadataSnapshot>(
                File.ReadAllText(filePath),
                JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(
        string filePath,
        DateTimeOffset refreshedAt,
        IReadOnlyList<SlackUserInfo> users,
        IReadOnlyList<SlackChannelInfo> channels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(channels);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var snapshot = new MomSlackMetadataSnapshot(
            refreshedAt,
            users.ToArray(),
            channels.ToArray());

        File.WriteAllText(
            filePath,
            JsonSerializer.Serialize(snapshot, JsonOptions));
    }
}
