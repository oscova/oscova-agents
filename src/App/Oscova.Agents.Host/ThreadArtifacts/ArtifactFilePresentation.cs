namespace Oscova.Agents.Host.Artifacts;

/// <summary>
/// Identifies the package-free preview surface used for a thread artifact.
/// </summary>
public enum ArtifactPreviewKind
{
    Markdown,
    Text,
    Image,
    Pdf,
    DownloadOnly
}

/// <summary>
/// Owns the explicit inline-preview allowlist and safe response content types.
/// </summary>
public static class ArtifactFilePresentation
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".json", ".jsonl", ".yaml", ".yml", ".toml", ".ini", ".xml", ".csv",
        ".cs", ".razor", ".cshtml", ".js", ".jsx", ".ts", ".tsx", ".css", ".scss", ".html", ".htm",
        ".sql", ".py", ".java", ".go", ".rs", ".sh", ".ps1", ".enc"
    };

    /// <summary>
    /// Selects the renderer for a file without executing active content.
    /// </summary>
    public static ArtifactPreviewKind GetPreviewKind(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            return ArtifactPreviewKind.Markdown;
        }

        if (TextExtensions.Contains(extension))
        {
            return ArtifactPreviewKind.Text;
        }

        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return ArtifactPreviewKind.Image;
        }

        return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? ArtifactPreviewKind.Pdf
            : ArtifactPreviewKind.DownloadOnly;
    }

    /// <summary>
    /// Returns whether a plain browser tab can show the file safely without an in-app viewer.
    /// </summary>
    public static bool OpensInBrowserTab(string fileName) =>
        GetPreviewKind(fileName) is ArtifactPreviewKind.Image or ArtifactPreviewKind.Text or ArtifactPreviewKind.Markdown;

    /// <summary>
    /// Returns the safe inline response content type for a supported preview.
    /// </summary>
    public static string GetInlineContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            _ => "text/plain; charset=utf-8"
        };
    }
}
