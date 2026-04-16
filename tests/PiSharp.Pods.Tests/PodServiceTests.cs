using PiSharp.Pods.Tests.Support;

namespace PiSharp.Pods.Tests;

public sealed class PodServiceTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-pod-service-{Guid.NewGuid():N}");

    [Fact]
    public async Task SetupPodAsync_StoresDetectedPodConfiguration()
    {
        var store = new PodsConfigurationStore(_rootDirectory);
        var transport = new FakePodSshTransport();
        transport.ExecuteResponses.Enqueue(new SshCommandResult("SSH OK\n", string.Empty, 0));
        transport.ExecuteResponses.Enqueue(new SshCommandResult(string.Empty, string.Empty, 0));
        transport.ExecuteResponses.Enqueue(new SshCommandResult("0, NVIDIA H100, 80 GB\n1, NVIDIA H100, 80 GB\n", string.Empty, 0));
        transport.StreamingResponses.Enqueue(
            new FakeStreamingResponse(
                0,
                [new SshOutputChunk(SshOutputStream.StandardOutput, "setup complete\n")]));

        var service = new PodService(
            store,
            transport,
            getEnvironmentVariable: name => name switch
            {
                "HF_TOKEN" => "hf-token",
                "PI_API_KEY" => "pi-key",
                _ => null,
            });

        var result = await service.SetupPodAsync(
            "dc1",
            "ssh root@1.2.3.4",
            new PodSetupRequest
            {
                ModelsPath = "/workspace",
                VllmVersion = PodsDefaults.VllmNightly,
            });

        Assert.Equal("dc1", result.PodName);
        Assert.Equal("/workspace", result.Pod.ModelsPath);
        Assert.Equal(PodsDefaults.VllmNightly, result.Pod.VllmVersion);
        Assert.Equal(2, result.Pod.Gpus.Count);

        var configuration = store.Load();
        Assert.Equal("dc1", configuration.Active);
        Assert.True(configuration.Pods.ContainsKey("dc1"));
        Assert.Contains(transport.ExecuteInvocations, invocation => invocation.Command.Contains("cat > /tmp/pisharp_pod_setup.sh", StringComparison.Ordinal));
        Assert.Contains(transport.StreamingInvocations, invocation => invocation.Command.Contains("bash /tmp/pisharp_pod_setup.sh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartModelAsync_UploadsScriptsAndPersistsDeployment()
    {
        var store = new PodsConfigurationStore(_rootDirectory);
        store.AddOrUpdatePod(
            "dc1",
            new PodDefinition
            {
                SshCommand = "ssh root@pod.example.com",
                Gpus =
                [
                    new GpuInfo { Id = 0, Name = "NVIDIA H100", Memory = "80 GB" },
                    new GpuInfo { Id = 1, Name = "NVIDIA H100", Memory = "80 GB" },
                ],
                Models = new Dictionary<string, ModelDeployment>(StringComparer.Ordinal),
                ModelsPath = "/workspace",
            });

        var transport = new FakePodSshTransport();
        transport.ExecuteResponses.Enqueue(new SshCommandResult(string.Empty, string.Empty, 0));
        transport.ExecuteResponses.Enqueue(new SshCommandResult("4242\n", string.Empty, 0));
        transport.StreamingResponses.Enqueue(
            new FakeStreamingResponse(
                130,
                [new SshOutputChunk(SshOutputStream.StandardOutput, "Application startup complete\n")]));

        var service = new PodService(
            store,
            transport,
            getEnvironmentVariable: name => name switch
            {
                "HF_TOKEN" => "hf-token",
                "PI_API_KEY" => "pi-key",
                _ => null,
            });

        var result = await service.StartModelAsync(
            new PodStartRequest
            {
                ModelId = "Qwen/Qwen2.5-Coder-32B-Instruct",
                Name = "qwen",
            });

        Assert.Equal("dc1", result.PodName);
        Assert.Equal(4242, result.ProcessId);
        Assert.Equal(new Uri("http://pod.example.com:8001/v1"), result.BaseUri);

        var configuration = store.Load();
        var deployment = configuration.Pods["dc1"].Models["qwen"];
        Assert.Equal("Qwen/Qwen2.5-Coder-32B-Instruct", deployment.ModelId);
        Assert.Equal(8001, deployment.Port);
        Assert.Equal(4242, deployment.ProcessId);
        Assert.Contains(transport.ExecuteInvocations, invocation => invocation.Command.Contains("cat > /tmp/pisharp_model_run_qwen.sh", StringComparison.Ordinal));
        Assert.Contains(transport.ExecuteInvocations, invocation => invocation.Command.Contains("setsid /tmp/pisharp_model_wrapper_qwen.sh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartModelAsync_RemovesDeploymentWhenStartupFails()
    {
        var store = new PodsConfigurationStore(_rootDirectory);
        store.AddOrUpdatePod(
            "dc1",
            new PodDefinition
            {
                SshCommand = "ssh root@pod.example.com",
                Gpus = [new GpuInfo { Id = 0, Name = "NVIDIA H100", Memory = "80 GB" }],
                Models = new Dictionary<string, ModelDeployment>(StringComparer.Ordinal),
                ModelsPath = "/workspace",
            });

        var transport = new FakePodSshTransport();
        transport.ExecuteResponses.Enqueue(new SshCommandResult(string.Empty, string.Empty, 0));
        transport.ExecuteResponses.Enqueue(new SshCommandResult("4343\n", string.Empty, 0));
        transport.StreamingResponses.Enqueue(
            new FakeStreamingResponse(
                130,
                [new SshOutputChunk(SshOutputStream.StandardError, "Model runner exiting with code 1\n")]));

        var service = new PodService(
            store,
            transport,
            getEnvironmentVariable: name => name switch
            {
                "HF_TOKEN" => "hf-token",
                "PI_API_KEY" => "pi-key",
                _ => null,
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartModelAsync(
                new PodStartRequest
                {
                    ModelId = "Qwen/Qwen2.5-Coder-32B-Instruct",
                    Name = "qwen",
                }));

        Assert.Contains("failed to start", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.Load().Pods["dc1"].Models);
    }

    [Fact]
    public async Task StartModelAsync_CanSkipStartupLogFollowing()
    {
        var store = new PodsConfigurationStore(_rootDirectory);
        store.AddOrUpdatePod(
            "dc1",
            new PodDefinition
            {
                SshCommand = "ssh root@pod.example.com",
                Gpus = [new GpuInfo { Id = 0, Name = "NVIDIA H100", Memory = "80 GB" }],
                Models = new Dictionary<string, ModelDeployment>(StringComparer.Ordinal),
                ModelsPath = "/workspace",
            });

        var transport = new FakePodSshTransport();
        transport.ExecuteResponses.Enqueue(new SshCommandResult(string.Empty, string.Empty, 0));
        transport.ExecuteResponses.Enqueue(new SshCommandResult("5252\n", string.Empty, 0));

        var service = new PodService(
            store,
            transport,
            getEnvironmentVariable: name => name switch
            {
                "HF_TOKEN" => "hf-token",
                "PI_API_KEY" => "pi-key",
                _ => null,
            });

        var result = await service.StartModelAsync(
            new PodStartRequest
            {
                ModelId = "Qwen/Qwen2.5-Coder-32B-Instruct",
                Name = "qwen",
                FollowStartupLogs = false,
            });

        Assert.Equal(5252, result.ProcessId);
        Assert.Empty(transport.StreamingInvocations);
        Assert.Equal(5252, store.Load().Pods["dc1"].Models["qwen"].ProcessId);
    }

    [Fact]
    public async Task StreamLogsAsync_UsesTailAndFollowOptions()
    {
        var store = new PodsConfigurationStore(_rootDirectory);
        store.AddOrUpdatePod(
            "dc1",
            new PodDefinition
            {
                SshCommand = "ssh root@pod.example.com",
                Gpus = [new GpuInfo { Id = 0, Name = "NVIDIA H100", Memory = "80 GB" }],
                Models = new Dictionary<string, ModelDeployment>(StringComparer.Ordinal)
                {
                    ["qwen"] = new()
                    {
                        ModelId = "Qwen/Qwen2.5-Coder-32B-Instruct",
                        Port = 8001,
                        GpuIds = [0],
                        ProcessId = 4242,
                    },
                },
                ModelsPath = "/workspace",
            });

        var transport = new FakePodSshTransport();
        transport.StreamingResponses.Enqueue(
            new FakeStreamingResponse(
                0,
                [new SshOutputChunk(SshOutputStream.StandardOutput, "log-line\n")]));

        var service = new PodService(store, transport);
        var chunks = new List<string>();

        await service.StreamLogsAsync(
            new PodLogsRequest
            {
                Name = "qwen",
                TailLines = 25,
                Follow = false,
            },
            (text, _, _) =>
            {
                chunks.Add(text);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(["log-line\n"], chunks);
        Assert.Contains(transport.StreamingInvocations, invocation => invocation.Command == "tail -n 25 ~/.vllm_logs/qwen.log");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}
