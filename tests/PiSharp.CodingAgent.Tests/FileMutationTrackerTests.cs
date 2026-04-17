namespace PiSharp.CodingAgent.Tests;

public sealed class FileMutationTrackerTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-file-mutations-{Guid.NewGuid():N}");

    public FileMutationTrackerTests()
    {
        Directory.CreateDirectory(_workingDirectory);
    }

    [Fact]
    public void Scan_CollectsModifiedFilesFromCommonShellPatterns()
    {
        var tracker = new FileMutationTracker(_workingDirectory);

        tracker.Scan(
            """
            + mv old.txt new.txt
            cp source.txt copies/copy.txt
            rm stale.txt
            echo hello > logs/output.txt
            cat notes.txt | tee logs/tee.txt > /dev/null
            """);

        Assert.Equal(
            ["old.txt", "new.txt", "copies/copy.txt", "stale.txt", "logs/output.txt", "logs/tee.txt"],
            tracker.ModifiedFiles);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }
}
