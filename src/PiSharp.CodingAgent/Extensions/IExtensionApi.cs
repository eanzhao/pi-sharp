using PiSharp.Agent;
using PiSharp.Ai;

namespace PiSharp.CodingAgent;

public interface IExtensionApi
{
    void RegisterTool(AgentTool tool, string? promptSnippet = null);

    void RegisterCommand(ExtensionCommand command);

    void RegisterShortcut(ExtensionShortcut shortcut);

    void RegisterFlag(ExtensionFlag flag);

    ValueTask SendMessage(string text, CancellationToken cancellationToken = default);

    void SetModel(ModelMetadata model);

    ThinkingLevel GetThinkingLevel();

    void SetThinkingLevel(ThinkingLevel thinkingLevel);
}
