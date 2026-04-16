using PiSharp.Mom;

namespace PiSharp.Mom.Tests;

public sealed class MomEventsWatcherTests : IDisposable
{
    private readonly string _workspaceDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-mom-events-{Guid.NewGuid():N}");

    [Fact]
    public async Task Start_DispatchesImmediateEventsAndDeletesFile()
    {
        SlackIncomingEvent? received = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = new MomEventsWatcher(
            _workspaceDirectory,
            (incomingEvent, _) =>
            {
                received = incomingEvent;
                completion.TrySetResult();
                return Task.CompletedTask;
            });

        watcher.Start();

        var eventsDirectory = Path.Combine(_workspaceDirectory, MomDefaults.EventsDirectoryName);
        var filePath = Path.Combine(eventsDirectory, "ticket.json");
        Directory.CreateDirectory(eventsDirectory);
        await File.WriteAllTextAsync(
            filePath,
            """
            {
              "type": "immediate",
              "channelId": "C123",
              "text": "New support ticket"
            }
            """);

        await WaitAsync(completion.Task);
        await WaitUntilAsync(static state => !File.Exists((string)state!), filePath);

        Assert.NotNull(received);
        Assert.Equal("C123", received!.ChannelId);
        Assert.Contains("New support ticket", received.Text);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task Start_DeletesPastOneShotEventsWithoutDispatching()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = new MomEventsWatcher(
            _workspaceDirectory,
            (_, _) =>
            {
                completion.TrySetResult();
                return Task.CompletedTask;
            });

        watcher.Start();

        var eventsDirectory = Path.Combine(_workspaceDirectory, MomDefaults.EventsDirectoryName);
        var filePath = Path.Combine(eventsDirectory, "past.json");
        Directory.CreateDirectory(eventsDirectory);
        await File.WriteAllTextAsync(
            filePath,
            """
            {
              "type": "one-shot",
              "channelId": "C123",
              "text": "Old reminder",
              "at": "2020-01-01T00:00:00+00:00"
            }
            """);

        await Task.Delay(500);

        Assert.False(completion.Task.IsCompleted);
        Assert.False(File.Exists(filePath));
    }

    private static async Task WaitAsync(Task task)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var completed = await Task.WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
        if (completed != task)
        {
            throw new TimeoutException("Timed out waiting for watcher callback.");
        }

        await task;
    }

    private static async Task WaitUntilAsync(Func<object?, bool> predicate, object? state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate(state))
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, timeout.Token);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceDirectory))
        {
            Directory.Delete(_workspaceDirectory, recursive: true);
        }
    }
}
