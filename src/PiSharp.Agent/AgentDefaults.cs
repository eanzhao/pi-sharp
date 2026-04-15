using PiSharp.Ai;

namespace PiSharp.Agent;

internal static class AgentDefaults
{
    public static readonly ModelMetadata UnknownModel = new(
        "unknown",
        "Unknown",
        new ApiId("unknown"),
        new ProviderId("unknown"),
        0,
        0,
        ModelCapability.None,
        ModelPricing.Free);
}
