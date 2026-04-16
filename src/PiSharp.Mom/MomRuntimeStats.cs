using System.Text.Json;

namespace PiSharp.Mom;

public sealed record MomRuntimeStatsSnapshot(
    int StartupBackfillChannels,
    int StartupBackfillMessages,
    DateTimeOffset? LastStartupBackfillAt,
    int ReconnectCount,
    DateTimeOffset? LastReconnectAt,
    int? LastReconnectGeneration,
    int BootstrapBackfillCount,
    int BootstrapBackfillMessages,
    int BootstrapBackfillFailures,
    DateTimeOffset? LastBootstrapBackfillAt,
    string? LastBootstrapBackfillChannel,
    int ReconnectGapBackfillCount,
    int ReconnectGapBackfillMessages,
    int ReconnectGapBackfillFailures,
    DateTimeOffset? LastReconnectGapBackfillAt,
    string? LastReconnectGapBackfillChannel);

public sealed class MomRuntimeStats
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _syncRoot = new();
    private readonly string? _persistencePath;
    private int _startupBackfillChannels;
    private int _startupBackfillMessages;
    private DateTimeOffset? _lastStartupBackfillAt;
    private int _reconnectCount;
    private DateTimeOffset? _lastReconnectAt;
    private int? _lastReconnectGeneration;
    private int _bootstrapBackfillCount;
    private int _bootstrapBackfillMessages;
    private int _bootstrapBackfillFailures;
    private DateTimeOffset? _lastBootstrapBackfillAt;
    private string? _lastBootstrapBackfillChannel;
    private int _reconnectGapBackfillCount;
    private int _reconnectGapBackfillMessages;
    private int _reconnectGapBackfillFailures;
    private DateTimeOffset? _lastReconnectGapBackfillAt;
    private string? _lastReconnectGapBackfillChannel;

    public MomRuntimeStats(string? persistencePath = null)
    {
        _persistencePath = string.IsNullOrWhiteSpace(persistencePath)
            ? null
            : Path.GetFullPath(persistencePath);

        if (_persistencePath is not null)
        {
            LoadFromFile(_persistencePath);
        }
    }

    public void RecordStartupBackfill(MomBackfillResult result, DateTimeOffset? occurredAt = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_syncRoot)
        {
            _startupBackfillChannels += result.ChannelsScanned;
            _startupBackfillMessages += result.MessagesLogged;
            _lastStartupBackfillAt = occurredAt ?? DateTimeOffset.UtcNow;
            PersistLocked();
        }
    }

    public void RecordReconnect(int generation, DateTimeOffset? occurredAt = null)
    {
        lock (_syncRoot)
        {
            _reconnectCount++;
            _lastReconnectAt = occurredAt ?? DateTimeOffset.UtcNow;
            _lastReconnectGeneration = generation;
            PersistLocked();
        }
    }

    public void RecordBootstrapBackfill(string channel, int messagesLogged, DateTimeOffset? occurredAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        lock (_syncRoot)
        {
            _bootstrapBackfillCount++;
            _bootstrapBackfillMessages += messagesLogged;
            _lastBootstrapBackfillAt = occurredAt ?? DateTimeOffset.UtcNow;
            _lastBootstrapBackfillChannel = channel;
            PersistLocked();
        }
    }

    public void RecordBootstrapBackfillFailure()
    {
        lock (_syncRoot)
        {
            _bootstrapBackfillFailures++;
            PersistLocked();
        }
    }

    public void RecordReconnectGapBackfill(string channel, int messagesLogged, DateTimeOffset? occurredAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        lock (_syncRoot)
        {
            _reconnectGapBackfillCount++;
            _reconnectGapBackfillMessages += messagesLogged;
            _lastReconnectGapBackfillAt = occurredAt ?? DateTimeOffset.UtcNow;
            _lastReconnectGapBackfillChannel = channel;
            PersistLocked();
        }
    }

    public void RecordReconnectGapBackfillFailure()
    {
        lock (_syncRoot)
        {
            _reconnectGapBackfillFailures++;
            PersistLocked();
        }
    }

    public MomRuntimeStatsSnapshot Snapshot()
    {
        lock (_syncRoot)
        {
            return BuildSnapshot();
        }
    }

    public string FormatSummary()
    {
        var snapshot = Snapshot();
        return
            $"Runtime stats: startup_channels={snapshot.StartupBackfillChannels} " +
            $"startup_messages={snapshot.StartupBackfillMessages} " +
            $"last_startup_backfill={FormatTimestamp(snapshot.LastStartupBackfillAt)} " +
            $"reconnects={snapshot.ReconnectCount} " +
            $"last_reconnect={FormatTimestamp(snapshot.LastReconnectAt)} " +
            $"last_reconnect_generation={snapshot.LastReconnectGeneration?.ToString() ?? "none"} " +
            $"bootstrap_backfills={snapshot.BootstrapBackfillCount} " +
            $"bootstrap_messages={snapshot.BootstrapBackfillMessages} " +
            $"bootstrap_failures={snapshot.BootstrapBackfillFailures} " +
            $"last_bootstrap_backfill={FormatTimestamp(snapshot.LastBootstrapBackfillAt)} " +
            $"last_bootstrap_channel={snapshot.LastBootstrapBackfillChannel ?? "none"} " +
            $"reconnect_gap_backfills={snapshot.ReconnectGapBackfillCount} " +
            $"reconnect_gap_messages={snapshot.ReconnectGapBackfillMessages} " +
            $"reconnect_gap_failures={snapshot.ReconnectGapBackfillFailures} " +
            $"last_reconnect_gap_backfill={FormatTimestamp(snapshot.LastReconnectGapBackfillAt)} " +
            $"last_reconnect_gap_channel={snapshot.LastReconnectGapBackfillChannel ?? "none"}";
    }

    private MomRuntimeStatsSnapshot BuildSnapshot() =>
        new(
            _startupBackfillChannels,
            _startupBackfillMessages,
            _lastStartupBackfillAt,
            _reconnectCount,
            _lastReconnectAt,
            _lastReconnectGeneration,
            _bootstrapBackfillCount,
            _bootstrapBackfillMessages,
            _bootstrapBackfillFailures,
            _lastBootstrapBackfillAt,
            _lastBootstrapBackfillChannel,
            _reconnectGapBackfillCount,
            _reconnectGapBackfillMessages,
            _reconnectGapBackfillFailures,
            _lastReconnectGapBackfillAt,
            _lastReconnectGapBackfillChannel);

    private void LoadFromFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            var snapshot = JsonSerializer.Deserialize<MomRuntimeStatsSnapshot>(
                File.ReadAllText(filePath),
                JsonOptions);
            if (snapshot is null)
            {
                return;
            }

            _startupBackfillChannels = snapshot.StartupBackfillChannels;
            _startupBackfillMessages = snapshot.StartupBackfillMessages;
            _lastStartupBackfillAt = snapshot.LastStartupBackfillAt;
            _reconnectCount = snapshot.ReconnectCount;
            _lastReconnectAt = snapshot.LastReconnectAt;
            _lastReconnectGeneration = snapshot.LastReconnectGeneration;
            _bootstrapBackfillCount = snapshot.BootstrapBackfillCount;
            _bootstrapBackfillMessages = snapshot.BootstrapBackfillMessages;
            _bootstrapBackfillFailures = snapshot.BootstrapBackfillFailures;
            _lastBootstrapBackfillAt = snapshot.LastBootstrapBackfillAt;
            _lastBootstrapBackfillChannel = snapshot.LastBootstrapBackfillChannel;
            _reconnectGapBackfillCount = snapshot.ReconnectGapBackfillCount;
            _reconnectGapBackfillMessages = snapshot.ReconnectGapBackfillMessages;
            _reconnectGapBackfillFailures = snapshot.ReconnectGapBackfillFailures;
            _lastReconnectGapBackfillAt = snapshot.LastReconnectGapBackfillAt;
            _lastReconnectGapBackfillChannel = snapshot.LastReconnectGapBackfillChannel;
        }
        catch
        {
            // Stats persistence is best-effort; ignore malformed or unreadable files.
        }
    }

    private void PersistLocked()
    {
        if (_persistencePath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_persistencePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            _persistencePath,
            JsonSerializer.Serialize(BuildSnapshot(), JsonOptions));
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp?.ToString("O") ?? "none";
}
