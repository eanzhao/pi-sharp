using System.Diagnostics;

namespace PiSharp.Pods;

public interface IPodShellLauncher
{
    Task<int> LaunchAsync(string sshCommand, CancellationToken cancellationToken = default);
}

public sealed class ProcessPodShellLauncher : IPodShellLauncher
{
    public async Task<int> LaunchAsync(string sshCommand, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sshCommand);

        var parts = sshCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new InvalidOperationException("SSH command was empty.");
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(parts[0], parts.Skip(1)),
            EnableRaisingEvents = true,
        };

        process.Start();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return 130;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string binary, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(binary)
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
