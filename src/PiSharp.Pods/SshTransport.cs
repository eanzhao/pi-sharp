using System.Diagnostics;
using System.Text;

namespace PiSharp.Pods;

public enum SshOutputStream
{
    StandardOutput,
    StandardError,
}

public sealed record SshOutputChunk(SshOutputStream Stream, string Text);

public sealed record SshCommandResult(string StandardOutput, string StandardError, int ExitCode);

public class SshCommandOptions
{
    public bool KeepAlive { get; init; }
}

public sealed class SshStreamingOptions : SshCommandOptions
{
    public bool ForceTty { get; init; }
}

public interface IPodSshTransport
{
    Task<SshCommandResult> ExecuteAsync(
        string sshCommand,
        string command,
        SshCommandOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<int> ExecuteStreamingAsync(
        string sshCommand,
        string command,
        Func<SshOutputChunk, CancellationToken, ValueTask>? onOutput = null,
        SshStreamingOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessPodSshTransport : IPodSshTransport
{
    public async Task<SshCommandResult> ExecuteAsync(
        string sshCommand,
        string command,
        SshCommandOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sshCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var (binary, arguments) = BuildSshInvocation(sshCommand, command, options, forceTty: false);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(binary, arguments, redirectOutput: true),
            EnableRaisingEvents = true,
        };

        process.Start();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new SshCommandResult(
            await standardOutputTask.ConfigureAwait(false),
            await standardErrorTask.ConfigureAwait(false),
            process.ExitCode);
    }

    public async Task<int> ExecuteStreamingAsync(
        string sshCommand,
        string command,
        Func<SshOutputChunk, CancellationToken, ValueTask>? onOutput = null,
        SshStreamingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sshCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var (binary, arguments) = BuildSshInvocation(sshCommand, command, options, forceTty: options?.ForceTty ?? false);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(binary, arguments, redirectOutput: true),
            EnableRaisingEvents = true,
        };

        process.Start();

        var stdoutTask = PumpAsync(process.StandardOutput, SshOutputStream.StandardOutput, onOutput, cancellationToken);
        var stderrTask = PumpAsync(process.StandardError, SshOutputStream.StandardError, onOutput, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return 130;
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        SshOutputStream stream,
        Func<SshOutputChunk, CancellationToken, ValueTask>? onOutput,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                if (onOutput is not null)
                {
                    await onOutput(new SshOutputChunk(stream, new string(buffer, 0, read)), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static ProcessStartInfo CreateStartInfo(string binary, IReadOnlyList<string> arguments, bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static (string Binary, List<string> Arguments) BuildSshInvocation(
        string sshCommand,
        string remoteCommand,
        SshCommandOptions? options,
        bool forceTty)
    {
        var parts = sshCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new InvalidOperationException("SSH command was empty.");
        }

        var binary = parts[0];
        var arguments = new List<string>(parts.Skip(1));

        if (forceTty && !arguments.Contains("-t", StringComparer.Ordinal))
        {
            arguments.Insert(0, "-t");
        }

        if (options?.KeepAlive == true)
        {
            arguments.InsertRange(
                0,
                [
                    "-o",
                    "ServerAliveInterval=30",
                    "-o",
                    "ServerAliveCountMax=120",
                ]);
        }

        arguments.Add(remoteCommand);
        return (binary, arguments);
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
