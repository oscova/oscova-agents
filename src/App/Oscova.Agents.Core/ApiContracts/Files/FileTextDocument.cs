namespace Oscova.Agents.Core.ApiContracts.Files;

/// <summary>Returns the current text content for a file in the user workspace.</summary>
public sealed class FileTextDocument
{
    public string Path { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsEncrypted { get; set; }
}
