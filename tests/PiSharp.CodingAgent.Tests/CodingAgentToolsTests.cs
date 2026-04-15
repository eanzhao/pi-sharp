using Microsoft.Extensions.AI;
using PiSharp.Agent;
using System.Text.Json;

namespace PiSharp.CodingAgent.Tests;

public sealed class CodingAgentToolsTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-coding-agent-{Guid.NewGuid():N}");

    public CodingAgentToolsTests()
    {
        Directory.CreateDirectory(_workingDirectory);
    }

    [Fact]
    public async Task ReadWriteEditTools_WorkInsideWorkingDirectory()
    {
        var tools = CodingAgentTools.CreateAll(_workingDirectory);

        await tools[BuiltInToolNames.Write].ExecuteAsync(
            "call-write",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["path"] = "notes.txt",
                    ["content"] = "alpha\nbeta\nbeta",
                }));

        var readResult = await tools[BuiltInToolNames.Read].ExecuteAsync(
            "call-read",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["path"] = "notes.txt",
                }));

        Assert.Equal("alpha\nbeta\nbeta", NormalizeScalarResult(readResult.Value));

        var editResult = await tools[BuiltInToolNames.Edit].ExecuteAsync(
            "call-edit",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["path"] = "notes.txt",
                    ["oldText"] = "alpha",
                    ["newText"] = "omega",
                }));

        Assert.Equal("Updated notes.txt.", NormalizeScalarResult(editResult.Value));
        Assert.Equal("omega\nbeta\nbeta", await File.ReadAllTextAsync(Path.Combine(_workingDirectory, "notes.txt")));
    }

    [Fact]
    public async Task ReadTool_BlocksPathTraversal()
    {
        var readTool = CodingAgentTools.CreateAll(_workingDirectory)[BuiltInToolNames.Read];

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            readTool.ExecuteAsync(
                "call-read",
                new AIFunctionArguments(
                    new Dictionary<string, object?>
                    {
                        ["path"] = "../outside.txt",
                    })).AsTask());

        Assert.Contains("escapes the working directory", exception.Message);
    }

    [Fact]
    public async Task LsFindAndGrep_ReturnExpectedMatches()
    {
        Directory.CreateDirectory(Path.Combine(_workingDirectory, "src"));
        await File.WriteAllTextAsync(Path.Combine(_workingDirectory, "src", "app.cs"), "Console.WriteLine(\"hello\");\n");
        await File.WriteAllTextAsync(Path.Combine(_workingDirectory, "README.md"), "hello repo\n");

        var tools = CodingAgentTools.CreateAll(_workingDirectory);

        var lsResult = await tools[BuiltInToolNames.Ls].ExecuteAsync(
            "call-ls",
            new AIFunctionArguments(new Dictionary<string, object?>()));
        var findResult = await tools[BuiltInToolNames.Find].ExecuteAsync(
            "call-find",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["pattern"] = "app",
                }));
        var grepResult = await tools[BuiltInToolNames.Grep].ExecuteAsync(
            "call-grep",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["pattern"] = "hello",
                }));

        Assert.Contains("README.md", NormalizeScalarResult(lsResult.Value));
        Assert.Contains("src/", NormalizeScalarResult(lsResult.Value));
        Assert.Contains("src/app.cs", NormalizeScalarResult(findResult.Value));
        Assert.Contains("README.md:1: hello repo", NormalizeScalarResult(grepResult.Value));
    }

    [Fact]
    public async Task BashTool_ExecutesCommand()
    {
        var command = OperatingSystem.IsWindows() ? "echo hello" : "printf hello";
        var bashTool = CodingAgentTools.CreateAll(_workingDirectory)[BuiltInToolNames.Bash];

        var result = await bashTool.ExecuteAsync(
            "call-bash",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["command"] = command,
                }));

        var text = NormalizeScalarResult(result.Value);
        Assert.Contains("Exit code: 0", text);
        Assert.Contains("hello", text);
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
