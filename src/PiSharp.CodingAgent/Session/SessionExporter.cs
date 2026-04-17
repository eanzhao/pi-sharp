using System.Text;

namespace PiSharp.CodingAgent;

public static class SessionExporter
{
    public static async Task ExportToHtmlAsync(SessionManager manager, string outputPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var branch = manager.GetBranch();
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset=\"utf-8\"><title>Session Export</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:system-ui,sans-serif;max-width:900px;margin:2rem auto;padding:0 1rem;line-height:1.6;}");
        sb.AppendLine(".msg{padding:0.8rem 1rem;border-radius:0.5rem;margin:0.5rem 0;}");
        sb.AppendLine(".user{background:#e0f2fe;border-left:3px solid #0284c7;}");
        sb.AppendLine(".assistant{background:#f0fdf4;border-left:3px solid #16a34a;}");
        sb.AppendLine(".tool{background:#f3f4f6;border-left:3px solid #6b7280;color:#4b5563;}");
        sb.AppendLine(".compaction{background:#fef3c7;border-left:3px solid #d97706;font-style:italic;}");
        sb.AppendLine(".role{font-size:0.75rem;font-weight:700;text-transform:uppercase;letter-spacing:0.05em;opacity:0.7;}");
        sb.AppendLine("pre{background:#1f2937;color:#e5e7eb;padding:0.8rem;border-radius:0.4rem;overflow-x:auto;}");
        sb.AppendLine("code{font-family:ui-monospace,monospace;}");
        sb.AppendLine("</style></head><body>");

        if (manager.Header is not null)
        {
            sb.Append("<h1>Session ").Append(HtmlEncode(manager.Header.Id)).AppendLine("</h1>");
            sb.Append("<p><code>").Append(HtmlEncode(manager.Header.Cwd)).AppendLine("</code></p>");
        }

        foreach (var entry in branch)
        {
            switch (entry)
            {
                case SessionMessageEntry msg:
                    var cssClass = msg.Role switch
                    {
                        "user" => "user",
                        "assistant" => "assistant",
                        _ => "tool",
                    };
                    sb.Append("<div class=\"msg ").Append(cssClass).Append("\"><div class=\"role\">");
                    sb.Append(HtmlEncode(msg.Role ?? "unknown")).Append("</div><pre>");
                    sb.Append(HtmlEncode(msg.Text ?? string.Empty)).AppendLine("</pre></div>");
                    break;
                case CompactionEntry comp:
                    sb.Append("<div class=\"msg compaction\"><div class=\"role\">compaction</div><p>");
                    sb.Append(HtmlEncode(comp.Summary)).AppendLine("</p></div>");
                    break;
            }
        }

        sb.AppendLine("</body></html>");

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), ct).ConfigureAwait(false);
    }

    private static string HtmlEncode(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}

public static class SessionMigrator
{
    public static int DetectVersion(string headerJson)
    {
        ArgumentNullException.ThrowIfNull(headerJson);
        using var doc = System.Text.Json.JsonDocument.Parse(headerJson);
        return doc.RootElement.TryGetProperty("version", out var v) ? v.GetInt32() : 1;
    }

    public static string MigrateEntry(string entryJson, int fromVersion, int toVersion)
    {
        if (fromVersion >= toVersion)
        {
            return entryJson;
        }

        var result = entryJson;
        for (var v = fromVersion; v < toVersion; v++)
        {
            result = ApplyMigration(result, v);
        }

        return result;
    }

    private static string ApplyMigration(string json, int fromVersion)
    {
        // v1→v2: role "toolResult" → "tool"
        return fromVersion switch
        {
            1 => json.Replace("\"role\":\"toolResult\"", "\"role\":\"tool\"", StringComparison.Ordinal),
            _ => json,
        };
    }
}

public sealed record BranchSummaryEntry : SessionEntry
{
    public required string Summary { get; init; }
    public required string FromLeafId { get; init; }
    public required string ToLeafId { get; init; }
}
