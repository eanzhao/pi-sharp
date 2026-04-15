using Microsoft.Extensions.AI;

namespace PiSharp.Pods.Tests;

public sealed class PodPromptingTests
{
    [Fact]
    public void Resolve_UsesActivePodAndDetectsResponsesApi()
    {
        var configuration = new PodsConfiguration
        {
            Active = "dc1",
            Pods = new Dictionary<string, PodDefinition>(StringComparer.Ordinal)
            {
                ["dc1"] = new PodDefinition
                {
                    SshCommand = "ssh -p 2222 root@1.2.3.4",
                    Models = new Dictionary<string, ModelDeployment>(StringComparer.Ordinal)
                    {
                        ["gpt"] = new ModelDeployment
                        {
                            ModelId = "openai/gpt-oss-20b",
                            Port = 8005,
                        },
                    },
                },
            },
        };

        var endpoint = new PodEndpointResolver().Resolve(configuration, "gpt", apiKey: "test-key");

        Assert.Equal("dc1", endpoint.PodName);
        Assert.Equal("openai/gpt-oss-20b", endpoint.ModelId);
        Assert.Equal(new Uri("http://1.2.3.4:8005/v1"), endpoint.BaseUri);
        Assert.Equal("test-key", endpoint.ApiKey);
        Assert.Equal(PodApiKind.Responses, endpoint.ApiKind);
    }

    [Fact]
    public void Create_ConfiguresAgentWithPromptAndDefaultTools()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-pods-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var capturedEndpoint = default(PodEndpoint);
            string? capturedApiKey = null;
            var fakeChatClient = new NoOpChatClient();

            var agent = new PodAgentFactory().Create(
                new PodEndpoint(
                    "dc1",
                    "qwen",
                    "Qwen/Qwen2.5-Coder-32B-Instruct",
                    new Uri("http://127.0.0.1:8001/v1"),
                    "default-key",
                    PodApiKind.ChatCompletions),
                new PodAgentFactoryOptions
                {
                    WorkingDirectory = workingDirectory,
                    ApiKey = "override-key",
                    CreateChatClient = (endpoint, apiKey) =>
                    {
                        capturedEndpoint = endpoint;
                        capturedApiKey = apiKey;
                        return fakeChatClient;
                    },
                });

            Assert.Equal("override-key", capturedApiKey);
            Assert.Equal("Qwen/Qwen2.5-Coder-32B-Instruct", capturedEndpoint!.ModelId);
            Assert.Equal("Qwen/Qwen2.5-Coder-32B-Instruct", agent.State.Model.Id);
            Assert.Equal(4, agent.State.Tools.Count);
            Assert.Contains(workingDirectory, agent.State.SystemPrompt);
            Assert.Contains("glob", agent.State.SystemPrompt);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private sealed class NoOpChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
