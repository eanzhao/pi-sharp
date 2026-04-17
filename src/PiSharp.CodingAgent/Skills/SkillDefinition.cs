using System.Text;

namespace PiSharp.CodingAgent;

public sealed record SkillDefinition(
    string Name,
    string Description,
    string Content);

public static class SkillParser
{
    public static SkillDefinition Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        string? name = null;
        string? description = null;
        var contentStart = 0;

        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line == "---")
                {
                    contentStart = i + 1;
                    break;
                }

                var colonIndex = line.IndexOf(':');
                if (colonIndex <= 0) continue;

                var key = line[..colonIndex].Trim();
                var value = line[(colonIndex + 1)..].Trim();

                if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
                {
                    name = value;
                }
                else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase))
                {
                    description = value;
                }
            }
        }

        var body = new StringBuilder();
        for (var i = contentStart; i < lines.Length; i++)
        {
            if (body.Length > 0) body.Append('\n');
            body.Append(lines[i]);
        }

        return new SkillDefinition(
            name ?? "unnamed",
            description ?? string.Empty,
            body.ToString().TrimStart('\n'));
    }
}

public sealed class SkillRegistry
{
    private readonly Dictionary<string, SkillDefinition> _skills = new(StringComparer.OrdinalIgnoreCase);

    public void Register(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        _skills[skill.Name] = skill;
    }

    public SkillDefinition? Get(string name) =>
        _skills.TryGetValue(name, out var skill) ? skill : null;

    public IReadOnlyCollection<SkillDefinition> GetAll() => _skills.Values;

    public static SkillRegistry LoadFromDirectory(string directory)
    {
        var registry = new SkillRegistry();
        if (!Directory.Exists(directory))
        {
            return registry;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "SKILL.md", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(file);
                registry.Register(SkillParser.Parse(content));
            }
            catch
            {
                // skip invalid
            }
        }

        return registry;
    }
}
