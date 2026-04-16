namespace PiSharp.Mom;

public sealed class MomConsoleEnvironment
{
    private readonly IReadOnlyDictionary<string, string?>? _environmentVariables;

    public MomConsoleEnvironment(
        TextReader input,
        TextWriter output,
        TextWriter error,
        string currentDirectory,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Output = output ?? throw new ArgumentNullException(nameof(output));
        Error = error ?? throw new ArgumentNullException(nameof(error));
        CurrentDirectory = Path.GetFullPath(currentDirectory ?? throw new ArgumentNullException(nameof(currentDirectory)));
        _environmentVariables = environmentVariables;
    }

    public TextReader Input { get; }

    public TextWriter Output { get; }

    public TextWriter Error { get; }

    public string CurrentDirectory { get; }

    public string? GetEnvironmentVariable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_environmentVariables is not null && _environmentVariables.TryGetValue(name, out var value))
        {
            return value;
        }

        return Environment.GetEnvironmentVariable(name);
    }

    public string GetHomeDirectory()
    {
        var home =
            GetEnvironmentVariable("HOME")
            ?? GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return string.IsNullOrWhiteSpace(home)
            ? CurrentDirectory
            : Path.GetFullPath(home);
    }

    public static MomConsoleEnvironment CreateProcessEnvironment() =>
        new(
            Console.In,
            Console.Out,
            Console.Error,
            Directory.GetCurrentDirectory());
}
