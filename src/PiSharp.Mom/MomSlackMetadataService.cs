namespace PiSharp.Mom;

public sealed class MomSlackMetadataService : IDisposable
{
    private readonly ISlackWorkspaceMetadataClient _slackClient;
    private readonly MomSlackWorkspaceIndex _workspaceIndex;
    private readonly string? _persistencePath;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _refreshInterval;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private long _lastRefreshUtcTicks;
    private bool _disposed;

    public MomSlackMetadataService(
        ISlackWorkspaceMetadataClient slackClient,
        MomSlackWorkspaceIndex workspaceIndex,
        TimeProvider? timeProvider = null,
        TimeSpan? refreshInterval = null,
        string? persistencePath = null)
    {
        _slackClient = slackClient ?? throw new ArgumentNullException(nameof(slackClient));
        _workspaceIndex = workspaceIndex ?? throw new ArgumentNullException(nameof(workspaceIndex));
        _persistencePath = string.IsNullOrWhiteSpace(persistencePath)
            ? null
            : Path.GetFullPath(persistencePath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _refreshInterval = refreshInterval ?? TimeSpan.FromMinutes(10);
        _lastRefreshUtcTicks = DateTimeOffset.MinValue.UtcTicks;

        var snapshot = _persistencePath is null
            ? null
            : MomSlackMetadataSnapshotStore.Load(_persistencePath);
        if (snapshot is not null)
        {
            _workspaceIndex.Update(snapshot.Users, snapshot.Channels);
            _lastRefreshUtcTicks = snapshot.RefreshedAt.UtcTicks;
        }
    }

    public DateTimeOffset LastRefreshAt => new(Volatile.Read(ref _lastRefreshUtcTicks), TimeSpan.Zero);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<bool> RefreshIfNeededAsync(
        string? userId,
        string? channelId,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldRefresh(userId, channelId))
        {
            return false;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ShouldRefresh(userId, channelId))
            {
                return false;
            }

            await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _refreshGate.Dispose();
        _disposed = true;
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var users = await _slackClient.GetUsersAsync(cancellationToken).ConfigureAwait(false);
        var channels = await _slackClient.GetChannelsAsync(users, cancellationToken).ConfigureAwait(false);
        _workspaceIndex.Update(users, channels);
        var refreshedAt = GetUtcNow();
        Interlocked.Exchange(ref _lastRefreshUtcTicks, refreshedAt.UtcTicks);

        if (_persistencePath is not null)
        {
            try
            {
                MomSlackMetadataSnapshotStore.Save(_persistencePath, refreshedAt, users, channels);
            }
            catch
            {
                // Metadata persistence is best-effort; refresh should still succeed.
            }
        }
    }

    private bool ShouldRefresh(string? userId, string? channelId)
    {
        if (HasExpired())
        {
            return true;
        }

        if (ShouldLookUpUser(userId) && _workspaceIndex.FindUser(userId!) is null)
        {
            return true;
        }

        if (ShouldLookUpChannel(channelId) && _workspaceIndex.FindChannel(channelId!) is null)
        {
            return true;
        }

        return false;
    }

    private bool HasExpired() =>
        LastRefreshAt == DateTimeOffset.MinValue ||
        GetUtcNow() - LastRefreshAt >= _refreshInterval;

    private DateTimeOffset GetUtcNow() =>
        _timeProvider.GetUtcNow();

    private static bool ShouldLookUpUser(string? userId) =>
        !string.IsNullOrWhiteSpace(userId) &&
        (userId.StartsWith("U", StringComparison.Ordinal) || userId.StartsWith("W", StringComparison.Ordinal));

    private static bool ShouldLookUpChannel(string? channelId) =>
        !string.IsNullOrWhiteSpace(channelId) &&
        (channelId.StartsWith("C", StringComparison.Ordinal) ||
         channelId.StartsWith("G", StringComparison.Ordinal) ||
         channelId.StartsWith("D", StringComparison.Ordinal));
}
