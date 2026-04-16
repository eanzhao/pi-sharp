using Microsoft.Extensions.AI;

namespace PiSharp.CodingAgent;

public static class CodingAgentContextLoader
{
    private static readonly string[] CandidateFileNames = ["AGENTS.md", "CLAUDE.md"];
    private static readonly IReadOnlyDictionary<string, string> ImageMediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
        };

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

    public static async Task<IReadOnlyList<AIContent>> LoadFileArgumentContentsAsync(
        IEnumerable<string> fileArguments,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileArguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var contents = new List<AIContent>();

        foreach (var fileArgument in fileArguments)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
            if (TryGetImageMediaType(fullPath, out var mediaType))
            {
                var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                contents.Add(
                    new DataContent(bytes, mediaType)
                    {
                        Name = displayPath,
                    });
                continue;
            }

            var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            AppendTextContent(contents, $"# File: {displayPath}\n\n{content}");
        }

        return contents;
    }

    public static bool TryGetImageMediaType(string path, out string mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ImageMediaTypes.TryGetValue(Path.GetExtension(path), out mediaType!);
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

    private static void AppendTextContent(IList<AIContent> contents, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (contents.Count > 0 && contents[^1] is TextContent existing)
        {
            contents[^1] = new TextContent($"{existing.Text}\n\n{text}");
            return;
        }

        contents.Add(new TextContent(text));
    }
}
