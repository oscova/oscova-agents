namespace Oscova.Agents.Host.Services.UI;

/// <summary>
/// Maps file extensions to the shared Iconify token used by file browsing and artifact previews.
/// </summary>
public static class FileIconResolver
{
    /// <summary>
    /// Returns a bundled Lucide Iconify name for the supplied file.
    /// </summary>
    public static string Resolve(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".md" or ".markdown" or ".txt" or ".log" => "lucide:file-text",
            ".json" or ".jsonl" or ".yaml" or ".yml" or ".toml" or ".xml" => "lucide:file-code",
            ".cs" or ".razor" or ".cshtml" or ".js" or ".jsx" or ".ts" or ".tsx" or ".css" or ".scss" or
                ".html" or ".htm" or ".sql" or ".py" or ".java" or ".go" or ".rs" or ".sh" or ".ps1" => "lucide:file-code-2",
            ".csv" => "lucide:file-spreadsheet",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => "lucide:file-image",
            ".pdf" => "lucide:file-type-2",
            _ => "lucide:file"
        };
    }
}
