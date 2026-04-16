using Microsoft.Extensions.AI;
using PiSharp.Agent;
using System.Text.Json;

namespace PiSharp.CodingAgent.Tests;

public sealed class UnifiedDiffApplierTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-diff-{Guid.NewGuid():N}");

    public UnifiedDiffApplierTests()
    {
        Directory.CreateDirectory(_workingDirectory);
    }

    [Fact]
    public void Apply_UpdatesContent_WhenContextMatches()
    {
        var diff = """
            --- a/notes.txt
            +++ b/notes.txt
            @@ -1,2 +1,2 @@
             alpha
            -beta
            +gamma
            """;

        var updatedContent = UnifiedDiffApplier.Apply("alpha\nbeta\n", diff);

        Assert.Equal("alpha\ngamma\n", updatedContent);
    }

    [Fact]
    public void Apply_ThrowsDetailedError_WhenContextDoesNotMatch()
    {
        var diff = """
            --- a/notes.txt
            +++ b/notes.txt
            @@ -1,2 +1,2 @@
             alpha
            -beta
            +gamma
            """;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            UnifiedDiffApplier.Apply("alpha\nomega\n", diff));

        Assert.Contains("notes.txt", exception.Message);
        Assert.Contains("expected 'beta'", exception.Message);
        Assert.Contains("found 'omega'", exception.Message);
    }

    [Fact]
    public async Task EditDiffTool_AppliesMultiFilePatch()
    {
        await File.WriteAllTextAsync(Path.Combine(_workingDirectory, "first.txt"), "alpha\nbeta\n");
        await File.WriteAllTextAsync(Path.Combine(_workingDirectory, "second.txt"), "one\ntwo\n");

        var diff = """
            --- a/first.txt
            +++ b/first.txt
            @@ -1,2 +1,2 @@
             alpha
            -beta
            +gamma
            --- a/second.txt
            +++ b/second.txt
            @@ -1,2 +1,2 @@
             one
            -two
            +three
            """;

        var tool = CodingAgentTools.CreateAll(_workingDirectory)[BuiltInToolNames.EditDiff];
        var result = await tool.ExecuteAsync(
            "call-edit-diff",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["diff"] = diff,
                }));

        var text = NormalizeScalarResult(result.Value);
        Assert.Contains("Applied unified diff to 2 files", text);
        Assert.Contains("first.txt", text);
        Assert.Contains("second.txt", text);
        Assert.Equal("alpha\ngamma\n", await File.ReadAllTextAsync(Path.Combine(_workingDirectory, "first.txt")));
        Assert.Equal("one\nthree\n", await File.ReadAllTextAsync(Path.Combine(_workingDirectory, "second.txt")));
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
