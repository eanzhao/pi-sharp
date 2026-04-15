using Microsoft.Extensions.AI;
using PiSharp.Ai;

namespace PiSharp.WebUi.Tests.Support;

internal static class TestFixtures
{
    public static readonly ModelMetadata TestModel = new(
        "gpt-4.1-mini",
        "GPT-4.1 mini",
        ApiId.OpenAi,
        ProviderId.OpenAi,
        1_000_000,
        32_768,
        ModelCapability.TextInput | ModelCapability.Streaming | ModelCapability.ToolCalling | ModelCapability.Reasoning,
        ModelPricing.Free);

    public static ChatResponseUpdate CreateUpdate(
        AIContent content,
        ChatFinishReason? finishReason = null) =>
        new(ChatRole.Assistant, [content])
        {
            FinishReason = finishReason,
        };
}
