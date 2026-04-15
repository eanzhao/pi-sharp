using PiSharp.CodingAgent;

namespace PiSharp.Cli;

public static class CliContextLoader
{
    private static readonly string[] CandidateFileNames = ["AGENTS.md", "CLAUDE.md"];

    public static IReadOnlyList<CodingAgentContextFile> Load(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var resolvedWorkingDirectory = Path.GetFullPath(workingDirectory);
        var directories = EnumerateAncestors(resolvedWorkingDirectory);
        var contextFiles = new List<CodingAgentContextFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            var contextFile = TryLoadContextFile(directory);
            if (contextFile is null)
            {
                continue;
            }

            if (seenPaths.Add(contextFile.Path))
            {
                contextFiles.Add(contextFile);
            }
        }

        return contextFiles;
    }

    public static string ResolvePromptInput(string input, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var directPath = Path.GetFullPath(input);
        if (File.Exists(directPath))
        {
            return File.ReadAllText(directPath);
        }

        var workingDirectoryPath = Path.Combine(Path.GetFullPath(workingDirectory), input);
        if (File.Exists(workingDirectoryPath))
        {
            return File.ReadAllText(workingDirectoryPath);
        }

        return input;
    }

    public static string LoadFileArgumentText(IEnumerable<string> fileArguments, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileArguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var builder = new List<string>();
        foreach (var fileArgument in fileArguments)
        {
            if (string.IsNullOrWhiteSpace(fileArgument))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(workingDirectory, fileArgument));
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"File argument not found: {fileArgument}", fullPath);
            }

            var displayPath = Path.GetRelativePath(workingDirectory, fullPath).Replace('\\', '/');
            var content = File.ReadAllText(fullPath);
            builder.Add($"# File: {displayPath}\n\n{content}");
        }

        return string.Join("\n\n", builder);
    }

    private static IReadOnlyList<string> EnumerateAncestors(string workingDirectory)
    {
        var directories = new Stack<string>();
        var current = new DirectoryInfo(workingDirectory);

        while (current is not null)
        {
            directories.Push(current.FullName);
            current = current.Parent;
        }

        return directories.ToArray();
    }

    private static CodingAgentContextFile? TryLoadContextFile(string directory)
    {
        foreach (var candidateFileName in CandidateFileNames)
        {
            var candidatePath = Path.Combine(directory, candidateFileName);
            if (!File.Exists(candidatePath))
            {
                continue;
            }

            return new CodingAgentContextFile(
                candidatePath.Replace('\\', '/'),
                File.ReadAllText(candidatePath));
        }

        return null;
    }
}
