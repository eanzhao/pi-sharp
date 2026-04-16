using Microsoft.Extensions.AI;
using PiSharp.Agent;
using System.Text;
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
    public async Task LsFindAndGrep_RespectGitIgnoreAndFallbackIgnoredDirectories()
    {
        var parentDirectory = Path.Combine(_workingDirectory, "parent");
        var scopedWorkingDirectory = Path.Combine(parentDirectory, "workspace");
        Directory.CreateDirectory(scopedWorkingDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(parentDirectory, ".gitignore"),
            """
            # comment
            ignored.txt
            ignored-dir/
            *.secret
            !keep.secret
            """);

        Directory.CreateDirectory(Path.Combine(scopedWorkingDirectory, "visible"));
        Directory.CreateDirectory(Path.Combine(scopedWorkingDirectory, "ignored-dir"));
        Directory.CreateDirectory(Path.Combine(scopedWorkingDirectory, ".git"));
        Directory.CreateDirectory(Path.Combine(scopedWorkingDirectory, "bin"));
        await File.WriteAllTextAsync(Path.Combine(scopedWorkingDirectory, "visible", "app.cs"), "needle\n");
        await File.WriteAllTextAsync(Path.Combine(scopedWorkingDirectory, "ignored.txt"), "needle\n");
        await File.WriteAllTextAsync(Path.Combine(scopedWorkingDirectory, "hidden.secret"), "needle\n");
        await File.WriteAllTextAsync(Path.Combine(scopedWorkingDirectory, "keep.secret"), "needle\n");
        await File.WriteAllTextAsync(Path.Combine(scopedWorkingDirectory, "ignored-dir", "nested.txt"), "needle\n");
        await File.WriteAllTextAsync(Path.Combine(scopedWorkingDirectory, ".git", "config"), "needle\n");
        await File.WriteAllTextAsync(Path.Combine(scopedWorkingDirectory, "bin", "output.txt"), "needle\n");

        var tools = CodingAgentTools.CreateAll(scopedWorkingDirectory);

        var lsResult = NormalizeScalarResult((await tools[BuiltInToolNames.Ls].ExecuteAsync(
            "call-ls",
            new AIFunctionArguments(new Dictionary<string, object?>()))).Value);
        var findResult = NormalizeScalarResult((await tools[BuiltInToolNames.Find].ExecuteAsync(
            "call-find",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["pattern"] = "secret",
                }))).Value);
        var grepResult = NormalizeScalarResult((await tools[BuiltInToolNames.Grep].ExecuteAsync(
            "call-grep",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["pattern"] = "needle",
                }))).Value);

        Assert.Contains("visible/", lsResult);
        Assert.Contains("keep.secret", lsResult);
        Assert.DoesNotContain("ignored.txt", lsResult);
        Assert.DoesNotContain("ignored-dir/", lsResult);
        Assert.DoesNotContain(".git/", lsResult);
        Assert.DoesNotContain("bin/", lsResult);

        Assert.Equal("keep.secret", findResult);
        Assert.Contains("visible/app.cs:1: needle", grepResult);
        Assert.Contains("keep.secret:1: needle", grepResult);
        Assert.DoesNotContain("ignored.txt", grepResult);
        Assert.DoesNotContain("ignored-dir/nested.txt", grepResult);
        Assert.DoesNotContain(".git/config", grepResult);
        Assert.DoesNotContain("bin/output.txt", grepResult);
    }

    [Fact]
    public async Task ReadTool_ReturnsImageHintForImageFiles()
    {
        var imagePath = Path.Combine(_workingDirectory, "diagram.png");
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        await File.WriteAllBytesAsync(imagePath, imageBytes);

        var readResult = await CodingAgentTools.CreateAll(_workingDirectory)[BuiltInToolNames.Read].ExecuteAsync(
            "call-read-image",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["path"] = "diagram.png",
                }));

        Assert.Equal(
            "Image file detected: diagram.png (4 bytes). Use the file path in your response to reference it.",
            NormalizeScalarResult(readResult.Value));
    }

    [Fact]
    public async Task ReadTool_ReturnsPdfHintForPdfFiles()
    {
        var pdfPath = Path.Combine(_workingDirectory, "spec.pdf");
        var pdfBytes = Encoding.UTF8.GetBytes("%PDF-1.7");
        await File.WriteAllBytesAsync(pdfPath, pdfBytes);

        var readResult = await CodingAgentTools.CreateAll(_workingDirectory)[BuiltInToolNames.Read].ExecuteAsync(
            "call-read-pdf",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["path"] = "spec.pdf",
                }));

        Assert.Equal(
            $"PDF file detected: spec.pdf ({pdfBytes.Length} bytes). PDF text extraction not yet available.",
            NormalizeScalarResult(readResult.Value));
    }

    [Fact]
    public async Task ReadTool_ReturnsBinaryHintForBinaryFiles()
    {
        var binaryPath = Path.Combine(_workingDirectory, "blob.dat");
        var binaryBytes = new byte[] { 0xFF, 0xFE, 0xFD, 0x00 };
        await File.WriteAllBytesAsync(binaryPath, binaryBytes);

        var readResult = await CodingAgentTools.CreateAll(_workingDirectory)[BuiltInToolNames.Read].ExecuteAsync(
            "call-read-binary",
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["path"] = "blob.dat",
                }));

        Assert.Equal(
            "Binary file detected: blob.dat (4 bytes). Cannot display binary content.",
            NormalizeScalarResult(readResult.Value));
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
