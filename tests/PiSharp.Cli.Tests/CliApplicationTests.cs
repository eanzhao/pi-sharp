using Microsoft.Extensions.AI;
using PiSharp.Ai;

namespace PiSharp.Cli.Tests;

public sealed class CliApplicationTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-cli-app-{Guid.NewGuid():N}");

    [Fact]
    public async Task RunAsync_PrintsAssistantTextAndInjectsContextFilesIntoPrompt()
    {
        var repoDirectory = Path.Combine(_rootDirectory, "repo");
        Directory.CreateDirectory(repoDirectory);
        File.WriteAllText(Path.Combine(repoDirectory, "AGENTS.md"), "Repository rules.");

        var output = new StringWriter();
        var error = new StringWriter();
        var fakeClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("done"), ChatFinishReason.Stop),
            ]);

        var providerCatalog = new CliProviderCatalog(
            [
                new CliProviderFactory
                {
                    Configuration = new ProviderConfiguration(
                        ProviderId.OpenAi,
                        ApiId.OpenAi,
                        "OpenAI",
                        DefaultModelId: "gpt-4.1-mini",
                        ApiKeyEnvironmentVariable: "OPENAI_API_KEY"),
                    KnownModels =
                    [
                        new ModelMetadata(
                            "gpt-4.1-mini",
                            "GPT-4.1 mini",
                            ApiId.OpenAi,
                            ProviderId.OpenAi,
                            1_000_000,
                            32_768,
                            ModelCapability.TextInput | ModelCapability.Streaming | ModelCapability.ToolCalling,
                            ModelPricing.Free),
                    ],
                    CreateChatClient = (_, _) => fakeClient,
                },
            ]);

        var environment = new CliEnvironment(
            new StringReader(string.Empty),
            output,
            error,
            repoDirectory,
            isInputRedirected: false,
            new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "test-key",
            });

        var application = new CliApplication(environment, providerCatalog);

        var exitCode = await application.RunAsync(["Summarize", "this", "repo"]);

        Assert.Equal(0, exitCode);
        Assert.Equal($"done{Environment.NewLine}", output.ToString());
        Assert.Contains("Repository rules.", fakeClient.Options[0].Instructions);
        Assert.Equal(4, fakeClient.Options[0].Tools!.Count);
    }

    [Fact]
    public async Task RunAsync_ListModels_PrintsKnownModels()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var providerCatalog = new CliProviderCatalog(
            [
                new CliProviderFactory
                {
                    Configuration = new ProviderConfiguration(
                        ProviderId.OpenAi,
                        ApiId.OpenAi,
                        "OpenAI",
                        DefaultModelId: "gpt-4.1-mini",
                        ApiKeyEnvironmentVariable: "OPENAI_API_KEY"),
                    KnownModels =
                    [
                        new ModelMetadata(
                            "gpt-4.1-mini",
                            "GPT-4.1 mini",
                            ApiId.OpenAi,
                            ProviderId.OpenAi,
                            1_000_000,
                            32_768,
                            ModelCapability.TextInput | ModelCapability.Streaming | ModelCapability.ToolCalling,
                            ModelPricing.Free),
                    ],
                    CreateChatClient = (_, _) => throw new NotSupportedException(),
                },
            ]);

        var environment = new CliEnvironment(
            new StringReader(string.Empty),
            output,
            error,
            _rootDirectory,
            isInputRedirected: false);

        var application = new CliApplication(environment, providerCatalog);

        var exitCode = await application.RunAsync(["--list-models"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("provider", output.ToString());
        Assert.Contains("gpt-4.1-mini", output.ToString());
    }

    private static ChatResponseUpdate CreateUpdate(
        AIContent content,
        ChatFinishReason? finishReason = null) =>
        new(ChatRole.Assistant, [content])
        {
            FinishReason = finishReason,
        };

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}
