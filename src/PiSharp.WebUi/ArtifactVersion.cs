namespace PiSharp.WebUi;

public sealed record ArtifactVersion(
    string ArtifactId,
    int VersionNumber,
    string ContentType,
    string Content)
{
    public string NormalizedContentType =>
        NormalizeContentType(ContentType)
        ?? throw new InvalidOperationException($"Unsupported artifact content type '{ContentType}'.");

    internal static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        return contentType.Trim().ToLowerInvariant() switch
        {
            "html" or "text/html" => "html",
            "svg" or "image/svg+xml" => "svg",
            "markdown" or "md" or "text/markdown" => "markdown",
            _ => null,
        };
    }
}
