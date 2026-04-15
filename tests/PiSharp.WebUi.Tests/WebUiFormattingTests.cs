using PiSharp.Ai;

namespace PiSharp.WebUi.Tests;

public sealed class WebUiFormattingTests
{
    [Fact]
    public void FormatUsage_IncludesTokenCountsAndTotalCost()
    {
        var usage = new ExtendedUsageDetails
        {
            InputTokenCount = 1_200,
            OutputTokenCount = 240,
            Cost = new UsageCostBreakdown(0.001m, 0.002m, 0m, 0m),
        };

        var formatted = PiSharp.WebUi.WebUiFormatting.FormatUsage(usage);

        Assert.Equal("↑1.2k ↓240 $0.0030", formatted);
    }
}
