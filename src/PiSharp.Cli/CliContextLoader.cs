using PiSharp.CodingAgent;

namespace PiSharp.Cli;

public static class CliContextLoader
{
    public static IReadOnlyList<CodingAgentContextFile> Load(string workingDirectory) =>
        CodingAgentContextLoader.Load(workingDirectory);

    public static string ResolvePromptInput(string input, string workingDirectory) =>
        CodingAgentContextLoader.ResolvePromptInput(input, workingDirectory);

    public static string LoadFileArgumentText(IEnumerable<string> fileArguments, string workingDirectory) =>
        CodingAgentContextLoader.LoadFileArgumentText(fileArguments, workingDirectory);
}
