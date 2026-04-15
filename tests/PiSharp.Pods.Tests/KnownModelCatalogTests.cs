namespace PiSharp.Pods.Tests;

public sealed class KnownModelCatalogTests
{
    private readonly KnownModelCatalog _catalog = KnownModelCatalog.LoadDefault();

    [Fact]
    public void LoadDefault_ContainsKnownModels()
    {
        Assert.True(_catalog.IsKnownModel("Qwen/Qwen2.5-Coder-32B-Instruct"));
        Assert.Equal("Qwen2.5-Coder-32B", _catalog.GetDisplayName("Qwen/Qwen2.5-Coder-32B-Instruct"));
    }

    [Fact]
    public void GetConfiguration_PrefersExactGpuTypeMatch()
    {
        var selection = _catalog.GetConfiguration(
            "openai/gpt-oss-20b",
            [new GpuInfo { Id = 0, Name = "NVIDIA B200", Memory = "180 GB" }],
            requestedGpuCount: 1);

        Assert.NotNull(selection);
        Assert.Contains("--async-scheduling", selection!.Arguments);
        Assert.Equal("1", selection.EnvironmentVariables["VLLM_USE_TRTLLM_ATTENTION"]);
    }

    [Fact]
    public void GetAvailableGpuCounts_ReturnsDistinctSortedValues()
    {
        var counts = _catalog.GetAvailableGpuCounts("openai/gpt-oss-120b");

        Assert.Equal([1, 2, 4, 8], counts);
    }
}
