namespace PiSharp.Pods.Tests.Support;

internal sealed class FakePodSshTransport : IPodSshTransport
{
    public Queue<SshCommandResult> ExecuteResponses { get; } = new();

    public Queue<FakeStreamingResponse> StreamingResponses { get; } = new();

    public List<FakeSshInvocation> ExecuteInvocations { get; } = [];

    public List<FakeSshInvocation> StreamingInvocations { get; } = [];

    public Task<SshCommandResult> ExecuteAsync(
        string sshCommand,
        string command,
        SshCommandOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ExecuteInvocations.Add(new FakeSshInvocation(sshCommand, command, options, false));

        if (ExecuteResponses.Count == 0)
        {
            throw new InvalidOperationException("No queued execute response.");
        }

        return Task.FromResult(ExecuteResponses.Dequeue());
    }

    public async Task<int> ExecuteStreamingAsync(
        string sshCommand,
        string command,
        Func<SshOutputChunk, CancellationToken, ValueTask>? onOutput = null,
        SshStreamingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        StreamingInvocations.Add(new FakeSshInvocation(sshCommand, command, options, true));

        if (StreamingResponses.Count == 0)
        {
            throw new InvalidOperationException("No queued streaming response.");
        }

        var response = StreamingResponses.Dequeue();
        foreach (var chunk in response.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onOutput is not null)
            {
                await onOutput(chunk, cancellationToken).ConfigureAwait(false);
            }
        }

        return response.ExitCode;
    }
}

internal sealed record FakeSshInvocation(
    string SshCommand,
    string Command,
    SshCommandOptions? Options,
    bool IsStreaming);

internal sealed record FakeStreamingResponse(
    int ExitCode,
    IReadOnlyList<SshOutputChunk> Chunks);
