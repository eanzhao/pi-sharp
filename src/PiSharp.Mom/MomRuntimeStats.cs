using System.Text.Json;
using System.Threading;

namespace PiSharp.Mom;

public sealed record MomRuntimeStatsSnapshot(
    int StartupBackfillChannels,
    int StartupBackfillMessages,
    int ReconnectCount,
    int BootstrapBackfillCount,
    int BootstrapBackfillMessages,
    int BootstrapBackfillFailures,
    int ReconnectGapBackfillCount,
    int ReconnectGapBackfillMessages,
    int ReconnectGapBackfillFailures);

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
    private int _reconnectCount;
    private int _bootstrapBackfillCount;
    private int _bootstrapBackfillMessages;
    private int _bootstrapBackfillFailures;
    private int _reconnectGapBackfillCount;
    private int _reconnectGapBackfillMessages;
    private int _reconnectGapBackfillFailures;

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

    public void RecordStartupBackfill(MomBackfillResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_syncRoot)
        {
            _startupBackfillChannels += result.ChannelsScanned;
            _startupBackfillMessages += result.MessagesLogged;
            PersistLocked();
        }
    }

    public void RecordReconnect()
    {
        lock (_syncRoot)
        {
            _reconnectCount++;
            PersistLocked();
        }
    }

    public void RecordBootstrapBackfill(int messagesLogged)
    {
        lock (_syncRoot)
        {
            _bootstrapBackfillCount++;
            _bootstrapBackfillMessages += messagesLogged;
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

    public void RecordReconnectGapBackfill(int messagesLogged)
    {
        lock (_syncRoot)
        {
            _reconnectGapBackfillCount++;
            _reconnectGapBackfillMessages += messagesLogged;
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
            $"reconnects={snapshot.ReconnectCount} " +
            $"bootstrap_backfills={snapshot.BootstrapBackfillCount} " +
            $"bootstrap_messages={snapshot.BootstrapBackfillMessages} " +
            $"bootstrap_failures={snapshot.BootstrapBackfillFailures} " +
            $"reconnect_gap_backfills={snapshot.ReconnectGapBackfillCount} " +
            $"reconnect_gap_messages={snapshot.ReconnectGapBackfillMessages} " +
            $"reconnect_gap_failures={snapshot.ReconnectGapBackfillFailures}";
    }

    private MomRuntimeStatsSnapshot BuildSnapshot() =>
        new(
            _startupBackfillChannels,
            _startupBackfillMessages,
            _reconnectCount,
            _bootstrapBackfillCount,
            _bootstrapBackfillMessages,
            _bootstrapBackfillFailures,
            _reconnectGapBackfillCount,
            _reconnectGapBackfillMessages,
            _reconnectGapBackfillFailures);

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
            _reconnectCount = snapshot.ReconnectCount;
            _bootstrapBackfillCount = snapshot.BootstrapBackfillCount;
            _bootstrapBackfillMessages = snapshot.BootstrapBackfillMessages;
            _bootstrapBackfillFailures = snapshot.BootstrapBackfillFailures;
            _reconnectGapBackfillCount = snapshot.ReconnectGapBackfillCount;
            _reconnectGapBackfillMessages = snapshot.ReconnectGapBackfillMessages;
            _reconnectGapBackfillFailures = snapshot.ReconnectGapBackfillFailures;
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
}
