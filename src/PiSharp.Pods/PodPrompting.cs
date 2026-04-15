using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using PiSharp.Agent;
using PiSharp.Ai;

namespace PiSharp.Pods;

public static class PodPromptSystemPrompt
{
    public static string Build(string workingDirectory)
    {
        var normalizedWorkingDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(workingDirectory)
                ? Directory.GetCurrentDirectory()
                : workingDirectory);

        return $$"""
You help the user understand and navigate the codebase in the current working directory.

You can use these tools:
- `ls`: list files and directories
- `read`: read text files with line numbers
- `glob`: find files by glob pattern
- `rg`: search file contents with regular expressions

Do not output file contents you read via the `read` tool directly, unless the user asks for them.

Do not output markdown tables as part of your responses.

Keep your responses concise and relevant to the user's request.

File paths you output must include line numbers where possible, e.g. `src/index.ts:10-20`.

Current working directory: {{normalizedWorkingDirectory}}
""";
    }
}

public sealed class PodEndpointResolver
{
    public PodEndpoint Resolve(
        PodsConfiguration configuration,
        string deploymentName,
        string? podName = null,
        string? apiKey = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        var resolvedPodName = !string.IsNullOrWhiteSpace(podName)
            ? podName
            : configuration.Active ?? throw new InvalidOperationException("No active pod is configured.");

        if (!configuration.Pods.TryGetValue(resolvedPodName, out var pod))
        {
            throw new KeyNotFoundException($"Pod '{resolvedPodName}' was not found.");
        }

        if (!pod.Models.TryGetValue(deploymentName, out var deployment))
        {
            throw new KeyNotFoundException($"Deployment '{deploymentName}' was not found on pod '{resolvedPodName}'.");
        }

        var host = SshCommandParser.ExtractHost(pod.SshCommand);
        var endpoint = new Uri($"http://{host}:{deployment.Port}/v1", UriKind.Absolute);
        var effectiveApiKey = !string.IsNullOrWhiteSpace(apiKey)
            ? apiKey
            : Environment.GetEnvironmentVariable(PodsDefaults.PiApiKeyEnvironmentVariable) ?? "dummy";

        return new PodEndpoint(
            resolvedPodName,
            deploymentName,
            deployment.ModelId,
            endpoint,
            effectiveApiKey,
            ResolveApiKind(deployment.ModelId));
    }

    private static PodApiKind ResolveApiKind(string modelId) =>
        modelId.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase)
            ? PodApiKind.Responses
            : PodApiKind.ChatCompletions;
}

public sealed class PodAgentFactory
{
    private static readonly ProviderId PodsProviderId = new("pods");

    public PiSharp.Agent.Agent Create(PodEndpoint endpoint, PodAgentFactoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        options ??= new PodAgentFactoryOptions();

        var workingDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(options.WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : options.WorkingDirectory);

        var effectivePrompt = string.IsNullOrWhiteSpace(options.SystemPrompt)
            ? PodPromptSystemPrompt.Build(workingDirectory)
            : options.SystemPrompt;

        var chatClient = options.CreateChatClient(endpoint, options.ApiKey ?? endpoint.ApiKey);
        var tools = PodPromptTools.CreateDefault(workingDirectory, options.ToolOptions);

        return new PiSharp.Agent.Agent(
            chatClient,
            new PiSharp.Agent.AgentOptions
            {
                Model = CreateModelMetadata(endpoint.ModelId),
                SystemPrompt = effectivePrompt,
                ThinkingLevel = options.ThinkingLevel,
                Tools = tools,
            });
    }

    internal static IChatClient CreateDefaultChatClient(PodEndpoint endpoint, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint.BaseUri,
        };

        return endpoint.ApiKind switch
        {
            PodApiKind.Responses => new OpenAI.Responses.ResponsesClient(new ApiKeyCredential(apiKey), clientOptions)
                .AsIChatClient(endpoint.ModelId),
            _ => new OpenAI.Chat.ChatClient(endpoint.ModelId, new ApiKeyCredential(apiKey), clientOptions)
                .AsIChatClient(),
        };
    }

    private static ModelMetadata CreateModelMetadata(string modelId) =>
        new(
            modelId,
            modelId,
            ApiId.OpenAi,
            PodsProviderId,
            0,
            0,
            ModelCapability.TextInput | ModelCapability.Streaming | ModelCapability.ToolCalling,
            ModelPricing.Free);
}

internal static class SshCommandParser
{
    public static string ExtractHost(string sshCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sshCommand);

        var parts = sshCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var loginToken = parts.FirstOrDefault(static part => part.Contains('@', StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(loginToken))
        {
            return loginToken[(loginToken.LastIndexOf('@') + 1)..];
        }

        for (var index = parts.Length - 1; index >= 0; index--)
        {
            var part = parts[index];
            if (part.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            if (index > 0 && IsOptionWithValue(parts[index - 1]))
            {
                continue;
            }

            if (string.Equals(part, "ssh", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return part;
        }

        throw new InvalidOperationException($"Could not extract an SSH host from '{sshCommand}'.");
    }

    private static bool IsOptionWithValue(string value) =>
        value is "-i" or "-p" or "-o" or "-F" or "-J" or "-l" or "-S" or "-W";
}
