namespace Oscova.Agents.Core.ApiContracts.Prompts;

/// <summary>Represents an editable prompt file with its current and default content.</summary>
public sealed class PromptDocument
{
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? DefaultContent { get; set; }
    public string? Mode { get; set; }
    public bool HasMetadataError { get; set; }
}
