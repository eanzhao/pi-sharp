using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.CodingAgent;

public sealed class FileMutationTracker
{
    private static readonly Regex RedirectionPattern = new(@"(?:^|\s)(?:\d+)?>>?\s*(?<path>(""[^""]+""|'[^']+'|\S+))", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _workingDirectory;
    private readonly List<string> _modifiedFiles = [];

    public FileMutationTracker(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    public IReadOnlyList<string> ModifiedFiles => _modifiedFiles;

    public void Scan(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = StripPrompt(rawLine);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            foreach (Match match in RedirectionPattern.Matches(line))
            {
                AddPath(match.Groups["path"].Value);
            }

            var tokens = Tokenize(line);
            if (tokens.Count == 0)
            {
                continue;
            }

            for (var index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];
                if (token is "|" or "||" or "&&" or ";")
                {
                    continue;
                }

                switch (token)
                {
                    case "mv":
                        AddMoveTargets(tokens, index + 1);
                        break;
                    case "cp":
                        AddCopyTarget(tokens, index + 1);
                        break;
                    case "rm":
                        AddTargets(tokens, index + 1);
                        break;
                    case "tee":
                        AddTeeTargets(tokens, index + 1);
                        break;
                }
            }
        }
    }

    private void AddMoveTargets(IReadOnlyList<string> tokens, int startIndex)
    {
        var operands = CollectOperands(tokens, startIndex);
        if (operands.Count < 2)
        {
            return;
        }

        foreach (var operand in operands)
        {
            AddPath(operand);
        }
    }

    private void AddCopyTarget(IReadOnlyList<string> tokens, int startIndex)
    {
        var operands = CollectOperands(tokens, startIndex);
        if (operands.Count == 0)
        {
            return;
        }

        AddPath(operands[^1]);
    }

    private void AddTargets(IReadOnlyList<string> tokens, int startIndex)
    {
        foreach (var operand in CollectOperands(tokens, startIndex))
        {
            AddPath(operand);
        }
    }

    private void AddTeeTargets(IReadOnlyList<string> tokens, int startIndex)
    {
        foreach (var operand in CollectOperands(tokens, startIndex))
        {
            if (operand == "-")
            {
                continue;
            }

            AddPath(operand);
        }
    }

    private static List<string> CollectOperands(IReadOnlyList<string> tokens, int startIndex)
    {
        var operands = new List<string>();

        for (var index = startIndex; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token is "|" or "||" or "&&" or ";" ||
                token.Contains('>') ||
                token.StartsWith('<'))
            {
                break;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            operands.Add(token);
        }

        return operands;
    }

    private void AddPath(string rawPath)
    {
        var candidate = Unquote(rawPath).Trim();
        if (string.IsNullOrWhiteSpace(candidate) ||
            string.Equals(candidate, "/dev/null", StringComparison.Ordinal))
        {
            return;
        }

        string fullPath;
        if (Path.IsPathRooted(candidate))
        {
            fullPath = Path.GetFullPath(candidate);
        }
        else
        {
            fullPath = Path.GetFullPath(Path.Combine(_workingDirectory, candidate));
        }

        var relativePath = Path.GetRelativePath(_workingDirectory, fullPath);
        if (relativePath == "." ||
            relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return;
        }

        var normalizedPath = relativePath.Replace('\\', '/');
        if (!_modifiedFiles.Contains(normalizedPath, StringComparer.Ordinal))
        {
            _modifiedFiles.Add(normalizedPath);
        }
    }

    private static string StripPrompt(string line)
    {
        var trimmed = line.Trim();
        while (trimmed.StartsWith('+') || trimmed.StartsWith('$'))
        {
            trimmed = trimmed[1..].TrimStart();
        }

        return trimmed;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static IReadOnlyList<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        var quote = '\0';
        var escaping = false;

        foreach (var character in line)
        {
            if (escaping)
            {
                builder.Append(character);
                escaping = false;
                continue;
            }

            if (quote == '\0' && character == '\\')
            {
                escaping = true;
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    builder.Append(character);
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                FlushToken(tokens, builder);
                continue;
            }

            if (character is '|' or ';')
            {
                FlushToken(tokens, builder);
                tokens.Add(character.ToString());
                continue;
            }

            if (character == '&')
            {
                FlushToken(tokens, builder);
                tokens.Add("&&");
                continue;
            }

            builder.Append(character);
        }

        FlushToken(tokens, builder);
        return tokens;
    }

    private static void FlushToken(ICollection<string> tokens, StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            return;
        }

        tokens.Add(builder.ToString());
        builder.Clear();
    }
}
