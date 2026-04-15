namespace PiSharp.Cli.Tests;

public sealed class CliContextLoaderTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-cli-context-{Guid.NewGuid():N}");

    [Fact]
    public void Load_CollectsNearestContextFilePerAncestorDirectory()
    {
        var repoDirectory = Path.Combine(_rootDirectory, "repo");
        var nestedDirectory = Path.Combine(repoDirectory, "src", "feature");
        Directory.CreateDirectory(nestedDirectory);

        File.WriteAllText(Path.Combine(_rootDirectory, "AGENTS.md"), "global");
        File.WriteAllText(Path.Combine(repoDirectory, "CLAUDE.md"), "repo");
        File.WriteAllText(Path.Combine(Path.Combine(repoDirectory, "src"), "AGENTS.md"), "src");

        var contextFiles = CliContextLoader.Load(nestedDirectory);

        Assert.Equal(3, contextFiles.Count);
        Assert.Equal("global", contextFiles[0].Content);
        Assert.Equal("repo", contextFiles[1].Content);
        Assert.Equal("src", contextFiles[2].Content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}
