using PiSharp.Pods.Tests.Support;

namespace PiSharp.Pods.Tests;

public sealed class PodsApplicationTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-pods-app-{Guid.NewGuid():N}");

    [Fact]
    public void GetHelpText_NamespacedMode_UsesPodsPrefix()
    {
        var helpText = PodsApplication.GetHelpText("pisharp", namespaced: true);

        Assert.Contains("pisharp pods start", helpText, StringComparison.Ordinal);
        Assert.Contains("pisharp pods setup", helpText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PodsCommand_PrintsConfiguredPods()
    {
        var store = new PodsConfigurationStore(_rootDirectory);
        store.AddOrUpdatePod(
            "dc1",
            new PodDefinition
            {
                SshCommand = "ssh root@1.2.3.4",
                Gpus = [new GpuInfo { Id = 0, Name = "NVIDIA H100", Memory = "80 GB" }],
                Models = new Dictionary<string, ModelDeployment>(StringComparer.Ordinal),
                ModelsPath = "/workspace",
            });

        var output = new StringWriter();
        var error = new StringWriter();
        var app = new PodsApplication(
            new PodsConsoleEnvironment(new StringReader(string.Empty), output, error, _rootDirectory, false),
            new PodService(store, new FakePodSshTransport()));

        var exitCode = await app.RunAsync(["pods"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Configured pods:", output.ToString());
        Assert.Contains("* dc1", output.ToString());
    }

    [Fact]
    public async Task RunAsync_StartWithoutModel_PrintsKnownModels()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var app = new PodsApplication(
            new PodsConsoleEnvironment(new StringReader(string.Empty), output, error, _rootDirectory, false),
            new PodService(new PodsConfigurationStore(_rootDirectory), new FakePodSshTransport()));

        var exitCode = await app.RunAsync(["start"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Known models:", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Qwen/Qwen2.5-Coder-32B-Instruct", output.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}
