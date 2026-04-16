namespace PiSharp.Pods.Tests.Support;

internal sealed class FakePodShellLauncher : IPodShellLauncher
{
    public List<string> Launches { get; } = [];

    public int ExitCode { get; set; }

    public Task<int> LaunchAsync(string sshCommand, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Launches.Add(sshCommand);
        return Task.FromResult(ExitCode);
    }
}
