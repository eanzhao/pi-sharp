using Microsoft.Extensions.AI;
using PiSharp.Agent;
using System.Text.Json;

namespace PiSharp.Pods.Tests;

public sealed class PodPromptToolsTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-pods-tools-{Guid.NewGuid():N}");

    public PodPromptToolsTests()
    {
        Directory.CreateDirectory(_workingDirectory);
    }

    [Fact]
    public async Task ReadGlobAndRg_ReturnExpectedMatches()
    {
        Directory.CreateDirectory(Path.Combine(_workingDirectory, "src"));
        await File.WriteAllTextAsync(Path.Combine(_workingDirectory, "src", "app.cs"), "alpha\nhello world\nomega\n");
        await File.WriteAllTextAsync(Path.Combine(_workingDirectory, "README.md"), "hello repo\n");

        var tools = PodPromptTools.CreateDefault(_workingDirectory).ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        var readResult = await tools["read"].ExecuteAsync(
            "call-read",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["path"] = "src/app.cs",
                    ["limit"] = 2,
                }));

        var globResult = await tools["glob"].ExecuteAsync(
            "call-glob",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["pattern"] = "**/*.cs",
                }));

        var rgResult = await tools["rg"].ExecuteAsync(
            "call-rg",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["pattern"] = "hello",
                }));

        Assert.Contains("1: alpha", NormalizeScalarResult(readResult.Value));
        Assert.Contains("2: hello world", NormalizeScalarResult(readResult.Value));
        Assert.Contains("src/app.cs", NormalizeScalarResult(globResult.Value));
        Assert.Contains("README.md:1: hello repo", NormalizeScalarResult(rgResult.Value));
    }

    [Fact]
    public async Task Read_BlocksPathTraversal()
    {
        var tools = PodPromptTools.CreateDefault(_workingDirectory).ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            tools["read"].ExecuteAsync(
                "call-read",
                new AIFunctionArguments(
                    new Dictionary<string, object?>
                    {
                        ["path"] = "../outside.txt",
                    })).AsTask());

        Assert.Contains("escapes the working directory", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    private static string NormalizeScalarResult(object? value) =>
        value switch
        {
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String => jsonElement.GetString() ?? string.Empty,
            string text => text,
            _ => string.Empty,
        };
}
