using PiSharp.Agent;
using PiSharp.Ai;

namespace PiSharp.CodingAgent.Tests;

public sealed class ExtensionRunnerTests
{
    private static readonly ModelMetadata BaseModel = new(
        "gpt-4.1-mini",
        "GPT-4.1 mini",
        ApiId.OpenAi,
        ProviderId.OpenAi,
        1_000_000,
        32_768,
        ModelCapability.TextInput | ModelCapability.Streaming | ModelCapability.ToolCalling,
        ModelPricing.Free);

    private static readonly ModelMetadata AlternateModel = new(
        "claude-3-7-sonnet-latest",
        "Claude 3.7 Sonnet",
        ApiId.Anthropic,
        ProviderId.Anthropic,
        200_000,
        8_192,
        ModelCapability.TextInput | ModelCapability.Streaming | ModelCapability.ToolCalling,
        ModelPricing.Free);

    [Fact]
    public async Task LoadAsync_RegistersToolsCommandsShortcutsFlagsAndStateOverrides()
    {
        var runner = new ExtensionRunner([new RunnerTestExtension()], BaseModel, ThinkingLevel.Low);
        var builder = new CodingAgentSessionBuilder(Path.GetTempPath(), Array.Empty<string>());

        Assert.IsAssignableFrom<IExtensionApi>(runner.Api);

        await runner.LoadAsync(builder);

        Assert.Equal(AlternateModel.Id, runner.CurrentModel.Id);
        Assert.Equal(ThinkingLevel.High, runner.CurrentThinkingLevel);
        Assert.Contains("echo", builder.AdditionalTools.Keys);
        Assert.Contains("runner", runner.Commands.Keys);
        Assert.Contains("ctrl+r", runner.Shortcuts.Keys);
        Assert.Contains("plan", runner.Flags.Keys);
    }

    private sealed class RunnerTestExtension : ICodingAgentExtension
    {
        public ValueTask ConfigureSessionAsync(
            CodingAgentSessionBuilder builder,
            IExtensionApi api,
            CancellationToken cancellationToken = default)
        {
            api.RegisterTool(AgentTool.Create((string text) => text, name: "echo"), "Echo tool");
            api.RegisterCommand(new ExtensionCommand("runner", "Show runner state", (_, _, _) => ValueTask.CompletedTask));
            api.RegisterShortcut(new ExtensionShortcut("ctrl+r", "Reload runner state", (_, _) => ValueTask.CompletedTask));
            api.RegisterFlag(new ExtensionFlag("plan", "Enable planning", "true"));
            api.SetModel(AlternateModel);
            api.SetThinkingLevel(ThinkingLevel.High);
            builder.AddPromptGuideline("Runner guideline.");
            return ValueTask.CompletedTask;
        }
    }
}
