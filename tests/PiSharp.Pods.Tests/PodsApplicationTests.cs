using Microsoft.Extensions.AI;
using PiSharp.Agent;
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
        Assert.Contains("--interactive", helpText, StringComparison.Ordinal);
        Assert.Contains("pisharp pods ssh", helpText, StringComparison.Ordinal);
        Assert.Contains("pisharp pods shell", helpText, StringComparison.Ordinal);
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

    [Fact]
    public async Task RunAsync_SshCommand_StreamsRemoteOutput()
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

        var transport = new FakePodSshTransport();
        transport.StreamingResponses.Enqueue(
            new FakeStreamingResponse(
                0,
                [new SshOutputChunk(SshOutputStream.StandardOutput, "gpu-ok\n")]));

        var output = new StringWriter();
        var error = new StringWriter();
        var app = new PodsApplication(
            new PodsConsoleEnvironment(new StringReader(string.Empty), output, error, _rootDirectory, false),
            new PodService(store, transport));

        var exitCode = await app.RunAsync(["ssh", "nvidia-smi"]);

        Assert.Equal(0, exitCode);
        Assert.Equal("gpu-ok\n", output.ToString());
        Assert.Contains(transport.StreamingInvocations, invocation => invocation.Command == "nvidia-smi");
    }

    [Fact]
    public async Task RunAsync_ShellCommand_LaunchesPodShell()
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

        var launcher = new FakePodShellLauncher();
        var output = new StringWriter();
        var error = new StringWriter();
        var app = new PodsApplication(
            new PodsConsoleEnvironment(new StringReader(string.Empty), output, error, _rootDirectory, false),
            new PodService(store, new FakePodSshTransport()),
            podShellLauncher: launcher);

        var exitCode = await app.RunAsync(["shell"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["ssh root@1.2.3.4"], launcher.Launches);
    }

    [Fact]
    public async Task RunAsync_AgentWithoutPrompt_StartsInteractiveModeWhenTerminalIsAvailable()
    {
        var store = new PodsConfigurationStore(_rootDirectory);
        store.AddOrUpdatePod(
            "dc1",
            new PodDefinition
            {
                SshCommand = "ssh root@1.2.3.4",
                Gpus = [new GpuInfo { Id = 0, Name = "NVIDIA H100", Memory = "80 GB" }],
                ModelsPath = "/workspace",
                Models = new Dictionary<string, ModelDeployment>(StringComparer.Ordinal)
                {
                    ["qwen"] = new()
                    {
                        ModelId = "Qwen/Qwen2.5-Coder-32B-Instruct",
                        Port = 8001,
                        GpuIds = [0],
                        ProcessId = 1234,
                    },
                },
            });

        var terminal = new FakeTerminal(80, 12);
        var output = new StringWriter();
        var error = new StringWriter();
        var keys = new Queue<ConsoleKeyInfo>(
        [
            new ConsoleKeyInfo('h', ConsoleKey.H, false, false, false),
            new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false),
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
            new ConsoleKeyInfo('/', ConsoleKey.Oem2, false, false, false),
            new ConsoleKeyInfo('e', ConsoleKey.E, false, false, false),
            new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false),
            new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false),
            new ConsoleKeyInfo('t', ConsoleKey.T, false, false, false),
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
        ]);

        var fakeClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("he")),
                CreateUpdate(new TextContent("llo"), ChatFinishReason.Stop),
            ]);

        var app = new PodsApplication(
            new PodsConsoleEnvironment(
                new StringReader(string.Empty),
                output,
                error,
                _rootDirectory,
                isInputRedirected: false,
                isOutputRedirected: false,
                terminal: terminal,
                readKey: _ => keys.Dequeue()),
            new PodService(store, new FakePodSshTransport()),
            new FakePodAgentFactory(
                (_, options) =>
                {
                    Assert.Equal(_rootDirectory, options!.WorkingDirectory);
                    return new PiSharp.Agent.Agent(fakeClient);
                }));

        var exitCode = await app.RunAsync(["agent", "qwen"]);

        Assert.Equal(0, exitCode);
        Assert.Single(fakeClient.Requests);
        Assert.Contains(fakeClient.Requests[0], message => message.Role == ChatRole.User && message.Text == "hi");
        Assert.Contains(terminal.Writes, write => write.Contains("Assistant> hello", StringComparison.Ordinal));
    }

    private static ChatResponseUpdate CreateUpdate(
        AIContent content,
        ChatFinishReason? finishReason = null) =>
        new(ChatRole.Assistant, [content])
        {
            FinishReason = finishReason,
        };

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}
