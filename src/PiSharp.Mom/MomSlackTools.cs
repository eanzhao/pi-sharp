using PiSharp.Agent;
using PiSharp.CodingAgent;

namespace PiSharp.Mom;

public static class MomSlackTools
{
    public static AgentTool CreateAttachTool(
        string workspaceDirectory,
        string channelDirectory,
        string channelId,
        ISlackMessagingClient slackClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(slackClient);

        var normalizedWorkspaceDirectory = Path.GetFullPath(workspaceDirectory);
        var normalizedChannelDirectory = Path.GetFullPath(channelDirectory);

        return AgentTool.Create(
            AttachAsync,
            name: "attach",
            description: "Upload a file from the workspace back to the current Slack channel.");

        async Task<string> AttachAsync(
            string label,
            string path,
            string? title = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(label);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var fullPath = ResolvePath(path, normalizedChannelDirectory);
            if (!IsWithinRoot(fullPath, normalizedWorkspaceDirectory))
            {
                throw new InvalidOperationException($"Only files inside '{normalizedWorkspaceDirectory}' can be attached.");
            }

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"File not found: {fullPath}", fullPath);
            }

            var effectiveTitle = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(fullPath) : title.Trim();
            await slackClient.UploadFileAsync(channelId, fullPath, effectiveTitle, cancellationToken).ConfigureAwait(false);
            return $"Attached file: {effectiveTitle} ({label.Trim()})";
        }
    }

    public static ICodingAgentExtension CreateAttachExtension(
        string workspaceDirectory,
        string channelDirectory,
        string channelId,
        ISlackMessagingClient slackClient) =>
        new AttachToolExtension(CreateAttachTool(workspaceDirectory, channelDirectory, channelId, slackClient));

    private static string ResolvePath(string path, string channelDirectory) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(channelDirectory, path));

    private static bool IsWithinRoot(string fullPath, string rootPath)
    {
        var relativePath = Path.GetRelativePath(rootPath, fullPath);
        return relativePath != ".." &&
            !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relativePath);
    }

    private sealed class AttachToolExtension(AgentTool tool) : ICodingAgentExtension
    {
        public ValueTask ConfigureSessionAsync(
            CodingAgentSessionBuilder builder,
            IExtensionApi api,
            CancellationToken cancellationToken = default)
        {
            api.RegisterTool(tool, "Upload a local file from the workspace back to the current Slack channel.");
            builder.AddPromptGuideline("Use attach when the right deliverable is a local file that should be shared back into Slack.");
            return ValueTask.CompletedTask;
        }
    }
}
