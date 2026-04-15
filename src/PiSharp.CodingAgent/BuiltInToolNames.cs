namespace PiSharp.CodingAgent;

public static class BuiltInToolNames
{
    public const string Read = "read";
    public const string Bash = "bash";
    public const string Edit = "edit";
    public const string Write = "write";
    public const string Grep = "grep";
    public const string Find = "find";
    public const string Ls = "ls";

    public static IReadOnlyList<string> Default { get; } =
    [
        Read,
        Bash,
        Edit,
        Write,
    ];

    public static IReadOnlyList<string> All { get; } =
    [
        Read,
        Bash,
        Edit,
        Write,
        Grep,
        Find,
        Ls,
    ];
}
