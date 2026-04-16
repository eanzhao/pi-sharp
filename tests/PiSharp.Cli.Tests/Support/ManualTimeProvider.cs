namespace PiSharp.Cli.Tests.Support;

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan elapsed) => _utcNow = _utcNow.Add(elapsed);
}
