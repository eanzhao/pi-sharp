namespace PiSharp.Pods.Tests;

public sealed class PodDeploymentPlannerTests
{
    private readonly PodDeploymentPlanner _planner = new();

    [Fact]
    public void Plan_KnownModel_SelectsLeastUsedGpuAndAppliesOverrides()
    {
        var pod = CreatePod();

        var plan = _planner.Plan(
            pod,
            new ModelDeploymentRequest
            {
                ModelId = "openai/gpt-oss-120b",
                Name = "gpt120",
                GpuCount = 1,
                Memory = "50%",
                ContextWindow = "32k",
            });

        Assert.Equal(8002, plan.Port);
        Assert.Equal([1], plan.GpuIds);
        Assert.Contains("--async-scheduling", plan.VllmArguments);
        Assert.Contains("--gpu-memory-utilization", plan.VllmArguments);
        Assert.Contains("0.5", plan.VllmArguments);
        Assert.Contains("--max-model-len", plan.VllmArguments);
        Assert.Contains("32768", plan.VllmArguments);
        Assert.DoesNotContain("0.95", plan.VllmArguments);
        Assert.True(plan.IsKnownModel);
    }

    [Fact]
    public void Plan_UnknownModel_RejectsExplicitGpuCount()
    {
        var pod = CreatePod();

        var exception = Assert.Throws<PodPlanningException>(() =>
            _planner.Plan(
                pod,
                new ModelDeploymentRequest
                {
                    ModelId = "some/unknown-model",
                    Name = "unknown",
                    GpuCount = 2,
                }));

        Assert.Contains("--gpus can only be used", exception.Message);
    }

    [Fact]
    public void Plan_UnknownModel_DefaultsToSingleLeastUsedGpu()
    {
        var pod = CreatePod();

        var plan = _planner.Plan(
            pod,
            new ModelDeploymentRequest
            {
                ModelId = "some/unknown-model",
                Name = "unknown",
            });

        Assert.Equal([1], plan.GpuIds);
        Assert.Empty(plan.EnvironmentVariables);
        Assert.False(plan.IsKnownModel);
    }

    private static PodDefinition CreatePod() =>
        new()
        {
            SshCommand = "ssh root@pod.example.com",
            Gpus =
            [
                new GpuInfo { Id = 0, Name = "NVIDIA H100", Memory = "80 GB" },
                new GpuInfo { Id = 1, Name = "NVIDIA H100", Memory = "80 GB" },
            ],
            Models = new Dictionary<string, ModelDeployment>(StringComparer.Ordinal)
            {
                ["existing"] = new ModelDeployment
                {
                    ModelId = "Qwen/Qwen2.5-Coder-32B-Instruct",
                    Port = 8001,
                    GpuIds = [0],
                    ProcessId = 42,
                },
            },
        };
}
