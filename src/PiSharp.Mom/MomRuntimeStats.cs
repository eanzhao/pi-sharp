using System.Text.RegularExpressions;
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
    DateTimeOffset? LastBootstrapBackfillFailureAt,
    string? LastBootstrapBackfillFailureChannel,
    string? LastBootstrapBackfillFailureReason,
    string? LastBootstrapBackfillFailureKind,
    int ReconnectGapBackfillCount,
    int ReconnectGapBackfillMessages,
    int ReconnectGapBackfillFailures,
    DateTimeOffset? LastReconnectGapBackfillAt,
    string? LastReconnectGapBackfillChannel,
    DateTimeOffset? LastReconnectGapBackfillFailureAt,
    string? LastReconnectGapBackfillFailureChannel,
    string? LastReconnectGapBackfillFailureReason,
    string? LastReconnectGapBackfillFailureKind);

public sealed class MomRuntimeStats
{
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);
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
    private DateTimeOffset? _lastBootstrapBackfillFailureAt;
    private string? _lastBootstrapBackfillFailureChannel;
    private string? _lastBootstrapBackfillFailureReason;
    private string? _lastBootstrapBackfillFailureKind;
    private int _reconnectGapBackfillCount;
    private int _reconnectGapBackfillMessages;
    private int _reconnectGapBackfillFailures;
    private DateTimeOffset? _lastReconnectGapBackfillAt;
    private string? _lastReconnectGapBackfillChannel;
    private DateTimeOffset? _lastReconnectGapBackfillFailureAt;
    private string? _lastReconnectGapBackfillFailureChannel;
    private string? _lastReconnectGapBackfillFailureReason;
    private string? _lastReconnectGapBackfillFailureKind;

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

    public void RecordBootstrapBackfillFailure(
        string channel,
        Exception exception,
        DateTimeOffset? occurredAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(exception);

        lock (_syncRoot)
        {
            _bootstrapBackfillFailures++;
            _lastBootstrapBackfillFailureAt = occurredAt ?? DateTimeOffset.UtcNow;
            _lastBootstrapBackfillFailureChannel = channel;
            _lastBootstrapBackfillFailureReason = SummarizeReason(exception.Message);
            _lastBootstrapBackfillFailureKind = ClassifyFailure(exception);
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

    public void RecordReconnectGapBackfillFailure(
        string channel,
        Exception exception,
        DateTimeOffset? occurredAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(exception);

        lock (_syncRoot)
        {
            _reconnectGapBackfillFailures++;
            _lastReconnectGapBackfillFailureAt = occurredAt ?? DateTimeOffset.UtcNow;
            _lastReconnectGapBackfillFailureChannel = channel;
            _lastReconnectGapBackfillFailureReason = SummarizeReason(exception.Message);
            _lastReconnectGapBackfillFailureKind = ClassifyFailure(exception);
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
            $"last_bootstrap_failure={FormatTimestamp(snapshot.LastBootstrapBackfillFailureAt)} " +
            $"last_bootstrap_failure_channel={snapshot.LastBootstrapBackfillFailureChannel ?? "none"} " +
            $"last_bootstrap_failure_reason={snapshot.LastBootstrapBackfillFailureReason ?? "none"} " +
            $"last_bootstrap_failure_kind={snapshot.LastBootstrapBackfillFailureKind ?? "unknown"} " +
            $"reconnect_gap_backfills={snapshot.ReconnectGapBackfillCount} " +
            $"reconnect_gap_messages={snapshot.ReconnectGapBackfillMessages} " +
            $"reconnect_gap_failures={snapshot.ReconnectGapBackfillFailures} " +
            $"last_reconnect_gap_backfill={FormatTimestamp(snapshot.LastReconnectGapBackfillAt)} " +
            $"last_reconnect_gap_channel={snapshot.LastReconnectGapBackfillChannel ?? "none"} " +
            $"last_reconnect_gap_failure={FormatTimestamp(snapshot.LastReconnectGapBackfillFailureAt)} " +
            $"last_reconnect_gap_failure_channel={snapshot.LastReconnectGapBackfillFailureChannel ?? "none"} " +
            $"last_reconnect_gap_failure_reason={snapshot.LastReconnectGapBackfillFailureReason ?? "none"} " +
            $"last_reconnect_gap_failure_kind={snapshot.LastReconnectGapBackfillFailureKind ?? "unknown"}";
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
            _lastBootstrapBackfillFailureAt,
            _lastBootstrapBackfillFailureChannel,
            _lastBootstrapBackfillFailureReason,
            _lastBootstrapBackfillFailureKind,
            _reconnectGapBackfillCount,
            _reconnectGapBackfillMessages,
            _reconnectGapBackfillFailures,
            _lastReconnectGapBackfillAt,
            _lastReconnectGapBackfillChannel,
            _lastReconnectGapBackfillFailureAt,
            _lastReconnectGapBackfillFailureChannel,
            _lastReconnectGapBackfillFailureReason,
            _lastReconnectGapBackfillFailureKind);

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
            _lastBootstrapBackfillFailureAt = snapshot.LastBootstrapBackfillFailureAt;
            _lastBootstrapBackfillFailureChannel = snapshot.LastBootstrapBackfillFailureChannel;
            _lastBootstrapBackfillFailureReason = snapshot.LastBootstrapBackfillFailureReason;
            _lastBootstrapBackfillFailureKind = snapshot.LastBootstrapBackfillFailureKind;
            _reconnectGapBackfillCount = snapshot.ReconnectGapBackfillCount;
            _reconnectGapBackfillMessages = snapshot.ReconnectGapBackfillMessages;
            _reconnectGapBackfillFailures = snapshot.ReconnectGapBackfillFailures;
            _lastReconnectGapBackfillAt = snapshot.LastReconnectGapBackfillAt;
            _lastReconnectGapBackfillChannel = snapshot.LastReconnectGapBackfillChannel;
            _lastReconnectGapBackfillFailureAt = snapshot.LastReconnectGapBackfillFailureAt;
            _lastReconnectGapBackfillFailureChannel = snapshot.LastReconnectGapBackfillFailureChannel;
            _lastReconnectGapBackfillFailureReason = snapshot.LastReconnectGapBackfillFailureReason;
            _lastReconnectGapBackfillFailureKind = snapshot.LastReconnectGapBackfillFailureKind;
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

    private static string ClassifyFailure(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return "cancelled";
        }

        if (exception is TimeoutException)
        {
            return "timeout";
        }

        if (exception is HttpRequestException)
        {
            return "network";
        }

        if (exception is IOException)
        {
            return "network";
        }

        if (exception is JsonException)
        {
            return "invalid_response";
        }

        if (exception is ArgumentException or FormatException)
        {
            return "window";
        }

        var message = exception.Message ?? string.Empty;
        if (ContainsAuthError(message))
        {
            return "auth";
        }

        if (message.StartsWith("Slack API '", StringComparison.Ordinal))
        {
            return "slack_api";
        }

        if (message.StartsWith("Slack response is missing '", StringComparison.Ordinal))
        {
            return "invalid_response";
        }

        if (ContainsWindowError(message))
        {
            return "window";
        }

        return "unknown";
    }

    private static string SummarizeReason(string reason)
    {
        var normalized = WhitespacePattern.Replace(reason ?? string.Empty, " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "unknown";
        }

        if (normalized.Length <= MomDefaults.RuntimeFailureReasonSummaryCharacterLimit)
        {
            return normalized;
        }

        return
            $"{normalized[..MomDefaults.RuntimeFailureReasonSummaryCharacterLimit].TrimEnd()}...";
    }

    private static bool ContainsAuthError(string message) =>
        message.Contains("invalid_auth", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("not_authed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("token_revoked", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("account_inactive", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("missing_scope", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("not allowed token type", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWindowError(string message) =>
        message.Contains("timestamp", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("oldest", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("latest", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("cutoff", StringComparison.OrdinalIgnoreCase);
}
