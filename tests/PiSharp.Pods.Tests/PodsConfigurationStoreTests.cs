namespace PiSharp.Pods.Tests;

public sealed class PodsConfigurationStoreTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-pods-config-{Guid.NewGuid():N}");

    [Fact]
    public void Load_ReturnsEmptyConfiguration_WhenFileDoesNotExist()
    {
        var store = new PodsConfigurationStore(_rootDirectory);

        var configuration = store.Load();

        Assert.Empty(configuration.Pods);
        Assert.Null(configuration.Active);
    }

    [Fact]
    public void AddOrUpdatePod_RoundTripsAndSetsInitialActivePod()
    {
        var store = new PodsConfigurationStore(_rootDirectory);
        var pod = CreatePod("ssh root@10.0.0.1");

        store.AddOrUpdatePod("dc1", pod);

        var configuration = store.Load();
        Assert.Equal("dc1", configuration.Active);
        Assert.True(configuration.Pods.TryGetValue("dc1", out var loadedPod));
        Assert.Equal("ssh root@10.0.0.1", loadedPod!.SshCommand);
        Assert.Single(loadedPod.Gpus);
        Assert.Equal(PodsDefaults.VllmRelease, loadedPod.VllmVersion);
    }

    [Fact]
    public void RemovePod_ClearsActivePod_WhenRemovingCurrentSelection()
    {
        var store = new PodsConfigurationStore(_rootDirectory);
        store.AddOrUpdatePod("dc1", CreatePod("ssh root@10.0.0.1"));

        store.RemovePod("dc1");

        var configuration = store.Load();
        Assert.Empty(configuration.Pods);
        Assert.Null(configuration.Active);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private static PodDefinition CreatePod(string sshCommand) =>
        new()
        {
            SshCommand = sshCommand,
            Gpus = [new GpuInfo { Id = 0, Name = "NVIDIA H100", Memory = "80 GB" }],
            Models = new Dictionary<string, ModelDeployment>(StringComparer.Ordinal),
        };
}
