using System.Text.Json;
using Cronos;

namespace PiSharp.Mom;

public sealed record MomEventEnvelope
{
    public required string Type { get; init; }

    public required string ChannelId { get; init; }

    public required string Text { get; init; }

    public string? At { get; init; }

    public string? Schedule { get; init; }

    public string? Timezone { get; init; }
}

public sealed class MomEventsWatcher : IDisposable
{
    private readonly string _eventsDirectory;
    private readonly Func<SlackIncomingEvent, CancellationToken, Task> _dispatchAsync;
    private readonly DateTimeOffset _startTime;
    private readonly Dictionary<string, Timer> _timers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Timer> _debounceTimers = new(StringComparer.Ordinal);
    private readonly object _syncRoot = new();
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public MomEventsWatcher(
        string workspaceDirectory,
        Func<SlackIncomingEvent, CancellationToken, Task> dispatchAsync,
        DateTimeOffset? startTime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        _dispatchAsync = dispatchAsync ?? throw new ArgumentNullException(nameof(dispatchAsync));
        _eventsDirectory = Path.Combine(Path.GetFullPath(workspaceDirectory), MomDefaults.EventsDirectoryName);
        _startTime = startTime ?? DateTimeOffset.UtcNow;
    }

    public string EventsDirectory => _eventsDirectory;

    public void Start(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        Directory.CreateDirectory(_eventsDirectory);
        ScanExistingFiles(cancellationToken);

        _watcher = new FileSystemWatcher(_eventsDirectory, "*.json")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };

        _watcher.Created += (_, args) => Debounce(args.Name, cancellationToken);
        _watcher.Changed += (_, args) => Debounce(args.Name, cancellationToken);
        _watcher.Renamed += (_, args) =>
        {
            CancelScheduled(args.OldName);
            Debounce(args.Name, cancellationToken);
        };
        _watcher.Deleted += (_, args) => CancelScheduled(args.Name);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_syncRoot)
        {
            foreach (var timer in _timers.Values)
            {
                timer.Dispose();
            }

            foreach (var timer in _debounceTimers.Values)
            {
                timer.Dispose();
            }

            _timers.Clear();
            _debounceTimers.Clear();
        }

        _watcher?.Dispose();
        _disposed = true;
    }

    private void ScanExistingFiles(CancellationToken cancellationToken)
    {
        foreach (var filePath in Directory.EnumerateFiles(_eventsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            _ = HandleFileAsync(Path.GetFileName(filePath), cancellationToken);
        }
    }

    private void Debounce(string? filename, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_debounceTimers.Remove(filename, out var existing))
            {
                existing.Dispose();
            }

            _debounceTimers[filename] = new Timer(
                async _ =>
                {
                    lock (_syncRoot)
                    {
                        if (_debounceTimers.Remove(filename, out var timer))
                        {
                            timer.Dispose();
                        }
                    }

                    await HandleFileAsync(filename, cancellationToken).ConfigureAwait(false);
                },
                null,
                dueTime: TimeSpan.FromMilliseconds(100),
                period: Timeout.InfiniteTimeSpan);
        }
    }

    private async Task HandleFileAsync(string filename, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(_eventsDirectory, filename);
        if (!File.Exists(filePath))
        {
            CancelScheduled(filename);
            return;
        }

        MomEventEnvelope? envelope = null;
        Exception? lastError = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                envelope = JsonSerializer.Deserialize<MomEventEnvelope>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
                break;
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        if (envelope is null)
        {
            DeleteFile(filePath);
            if (lastError is not null)
            {
                Console.Error.WriteLine($"Failed to parse event file '{filename}': {lastError.Message}");
            }
            return;
        }

        CancelScheduled(filename);

        switch (envelope.Type)
        {
            case "immediate":
                HandleImmediate(filename, filePath, envelope, cancellationToken);
                break;
            case "one-shot":
                HandleOneShot(filename, filePath, envelope, cancellationToken);
                break;
            case "periodic":
                HandlePeriodic(filename, filePath, envelope, cancellationToken);
                break;
            default:
                DeleteFile(filePath);
                break;
        }
    }

    private void HandleImmediate(string filename, string filePath, MomEventEnvelope envelope, CancellationToken cancellationToken)
    {
        var lastWriteTime = File.GetLastWriteTimeUtc(filePath);
        if (lastWriteTime < _startTime.UtcDateTime)
        {
            DeleteFile(filePath);
            return;
        }

        _ = ExecuteAsync(filename, filePath, envelope, deleteAfterExecution: true, scheduleInfo: "immediate", cancellationToken);
    }

    private void HandleOneShot(string filename, string filePath, MomEventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!DateTimeOffset.TryParse(envelope.At, out var at))
        {
            DeleteFile(filePath);
            return;
        }

        var delay = at - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            DeleteFile(filePath);
            return;
        }

        ScheduleTimer(
            filename,
            delay,
            async () => await ExecuteAsync(filename, filePath, envelope, true, at.ToString("O"), cancellationToken).ConfigureAwait(false));
    }

    private void HandlePeriodic(string filename, string filePath, MomEventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(envelope.Schedule) || string.IsNullOrWhiteSpace(envelope.Timezone))
        {
            DeleteFile(filePath);
            return;
        }

        CronExpression cronExpression;
        TimeZoneInfo timeZone;

        try
        {
            cronExpression = CronExpression.Parse(envelope.Schedule, CronFormat.Standard);
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(envelope.Timezone);
        }
        catch
        {
            DeleteFile(filePath);
            return;
        }

        void ScheduleNext()
        {
            var next = cronExpression.GetNextOccurrence(DateTimeOffset.UtcNow, timeZone);
            if (next is null)
            {
                return;
            }

            var delay = next.Value - DateTimeOffset.UtcNow;
            ScheduleTimer(
                filename,
                delay < TimeSpan.Zero ? TimeSpan.Zero : delay,
                async () =>
                {
                    await ExecuteAsync(filename, filePath, envelope, false, envelope.Schedule!, cancellationToken).ConfigureAwait(false);
                    if (File.Exists(filePath))
                    {
                        ScheduleNext();
                    }
                });
        }

        ScheduleNext();
    }

    private void ScheduleTimer(string filename, TimeSpan delay, Func<Task> callback)
    {
        lock (_syncRoot)
        {
            if (_timers.Remove(filename, out var existing))
            {
                existing.Dispose();
            }

            _timers[filename] = new Timer(
                async _ =>
                {
                    lock (_syncRoot)
                    {
                        if (_timers.Remove(filename, out var timer))
                        {
                            timer.Dispose();
                        }
                    }

                    await callback().ConfigureAwait(false);
                },
                null,
                dueTime: delay,
                period: Timeout.InfiniteTimeSpan);
        }
    }

    private async Task ExecuteAsync(
        string filename,
        string filePath,
        MomEventEnvelope envelope,
        bool deleteAfterExecution,
        string scheduleInfo,
        CancellationToken cancellationToken)
    {
        var prompt = envelope.Type switch
        {
            "immediate" => $"[EVENT:{filename}:immediate] {envelope.Text}",
            "one-shot" => $"[EVENT:{filename}:one-shot:{scheduleInfo}] {envelope.Text}",
            _ => $"[EVENT:{filename}:periodic:{scheduleInfo}] {envelope.Text}",
        };

        await _dispatchAsync(
                new SlackIncomingEvent(
                    envelope.ChannelId,
                    "EVENT",
                    prompt,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                    "event",
                    false,
                    QueueIfBusy: true,
                    StatusText: $"_Starting event: {filename}_"),
                cancellationToken)
            .ConfigureAwait(false);

        if (deleteAfterExecution)
        {
            DeleteFile(filePath);
        }
    }

    private void CancelScheduled(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_timers.Remove(filename, out var timer))
            {
                timer.Dispose();
            }

            if (_debounceTimers.Remove(filename, out var debounceTimer))
            {
                debounceTimer.Dispose();
            }
        }
    }

    private static void DeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
