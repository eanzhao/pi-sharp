using System.Collections.Concurrent;
using System.Text.Json;

namespace PiSharp.CodingAgent;

public sealed class SessionManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly List<SessionEntry> _entries = [];
    private readonly ConcurrentDictionary<string, SessionEntry> _byId = new(StringComparer.Ordinal);

    public SessionManager(string sessionDir, string cwd)
    {
        SessionDir = Path.GetFullPath(sessionDir);
        Cwd = Path.GetFullPath(cwd);
    }

    public string SessionDir { get; }
    public string Cwd { get; }
    public SessionHeader? Header { get; private set; }
    public string? SessionFile { get; private set; }
    public string? LeafId { get; private set; }
    public IReadOnlyList<SessionEntry> Entries => _entries;

    public string NewSession(string? parentSession = null)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..16];
        var timestamp = SessionEntry.Now();

        Header = new SessionHeader
        {
            Id = sessionId,
            Timestamp = timestamp,
            Cwd = Cwd,
            ParentSession = parentSession,
        };

        _entries.Clear();
        _byId.Clear();
        LeafId = null;

        Directory.CreateDirectory(SessionDir);
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{sessionId}.jsonl";
        SessionFile = Path.Combine(SessionDir, fileName);

        File.WriteAllText(SessionFile, JsonSerializer.Serialize(Header, JsonOptions) + "\n");

        return sessionId;
    }

    public async Task LoadSessionAsync(string sessionFile, CancellationToken ct = default)
    {
        SessionFile = Path.GetFullPath(sessionFile);
        _entries.Clear();
        _byId.Clear();
        LeafId = null;

        var lines = await File.ReadAllLinesAsync(SessionFile, ct).ConfigureAwait(false);
        if (lines.Length == 0)
        {
            throw new InvalidOperationException("Session file is empty.");
        }

        Header = JsonSerializer.Deserialize<SessionHeader>(lines[0], JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize session header.");

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<SessionEntry>(line, JsonOptions);
            if (entry is null)
            {
                continue;
            }

            _entries.Add(entry);
            _byId[entry.Id] = entry;
            LeafId = entry.Id;
        }
    }

    public void AppendEntry(SessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrEmpty(entry.ParentId) && LeafId is not null)
        {
            entry = entry with { ParentId = LeafId };
        }

        _entries.Add(entry);
        _byId[entry.Id] = entry;
        LeafId = entry.Id;

        if (SessionFile is not null)
        {
            File.AppendAllText(SessionFile, JsonSerializer.Serialize<SessionEntry>(entry, JsonOptions) + "\n");
        }
    }

    public SessionEntry? GetEntry(string id) =>
        _byId.TryGetValue(id, out var entry) ? entry : null;

    public IReadOnlyList<SessionEntry> GetBranch(string? leafId = null)
    {
        leafId ??= LeafId;
        if (leafId is null)
        {
            return Array.Empty<SessionEntry>();
        }

        var branch = new List<SessionEntry>();
        var currentId = leafId;

        while (currentId is not null && _byId.TryGetValue(currentId, out var entry))
        {
            branch.Add(entry);
            currentId = entry.ParentId;
        }

        branch.Reverse();
        return branch;
    }

    public void SetLeaf(string entryId)
    {
        if (!_byId.ContainsKey(entryId))
        {
            throw new KeyNotFoundException($"Entry '{entryId}' not found.");
        }

        LeafId = entryId;
    }

    public SessionContext BuildContext(string? leafId = null)
    {
        var branch = GetBranch(leafId);
        var messages = new List<SessionMessageEntry>();
        string? thinkingLevel = null;
        string? providerId = null;
        string? modelId = null;

        CompactionEntry? lastCompaction = null;
        var firstKeptIndex = 0;

        for (var i = branch.Count - 1; i >= 0; i--)
        {
            if (branch[i] is CompactionEntry compaction)
            {
                lastCompaction = compaction;
                firstKeptIndex = i + 1;
                break;
            }
        }

        if (lastCompaction is not null)
        {
            messages.Add(new SessionMessageEntry
            {
                Id = SessionEntry.NewId(),
                Timestamp = lastCompaction.Timestamp,
                Role = "assistant",
                Text = lastCompaction.Summary,
            });
        }

        for (var i = firstKeptIndex; i < branch.Count; i++)
        {
            switch (branch[i])
            {
                case SessionMessageEntry msg:
                    messages.Add(msg);
                    break;
                case ThinkingLevelChangeEntry tlc:
                    thinkingLevel = tlc.Level;
                    break;
                case ModelChangeEntry mc:
                    providerId = mc.ProviderId;
                    modelId = mc.ModelId;
                    break;
            }
        }

        return new SessionContext(messages, thinkingLevel, providerId, modelId);
    }
}
