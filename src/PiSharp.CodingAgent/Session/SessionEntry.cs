using System.Text.Json.Serialization;

namespace PiSharp.CodingAgent;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionMessageEntry), "message")]
[JsonDerivedType(typeof(ThinkingLevelChangeEntry), "thinkingLevelChange")]
[JsonDerivedType(typeof(ModelChangeEntry), "modelChange")]
[JsonDerivedType(typeof(CompactionEntry), "compaction")]
[JsonDerivedType(typeof(LabelEntry), "label")]
public abstract record SessionEntry
{
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public required string Timestamp { get; init; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
    public static string Now() => DateTimeOffset.UtcNow.ToString("O");
}

public sealed record SessionHeader
{
    [JsonPropertyName("type")]
    public string Type => "session";
    public int Version { get; init; } = 1;
    public required string Id { get; init; }
    public required string Timestamp { get; init; }
    public required string Cwd { get; init; }
    public string? ParentSession { get; init; }
}

public sealed record SessionMessageEntry : SessionEntry
{
    public required string Role { get; init; }
    public required string Text { get; init; }
}

public sealed record ThinkingLevelChangeEntry : SessionEntry
{
    public required string Level { get; init; }
}

public sealed record ModelChangeEntry : SessionEntry
{
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
}

public sealed record CompactionEntry : SessionEntry
{
    public required string Summary { get; init; }
    public required string FirstKeptEntryId { get; init; }
    public required int TokensBefore { get; init; }
}

public sealed record LabelEntry : SessionEntry
{
    public required string TargetEntryId { get; init; }
    public string? Label { get; init; }
}

public record SessionContext(
    IReadOnlyList<SessionMessageEntry> Messages,
    string? ThinkingLevel,
    string? ProviderId,
    string? ModelId);
