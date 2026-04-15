using Microsoft.Extensions.AI;

namespace PiSharp.Agent;

public enum ThinkingLevel
{
    Off,
    Minimal,
    Low,
    Medium,
    High,
    ExtraHigh,
}

internal static class ThinkingLevelExtensions
{
    public static ReasoningOptions? ToReasoningOptions(this ThinkingLevel level) =>
        level switch
        {
            ThinkingLevel.Off => null,
            ThinkingLevel.Minimal => new ReasoningOptions { Effort = ReasoningEffort.Low },
            ThinkingLevel.Low => new ReasoningOptions { Effort = ReasoningEffort.Low },
            ThinkingLevel.Medium => new ReasoningOptions { Effort = ReasoningEffort.Medium },
            ThinkingLevel.High => new ReasoningOptions { Effort = ReasoningEffort.High },
            ThinkingLevel.ExtraHigh => new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
            _ => null,
        };
}
