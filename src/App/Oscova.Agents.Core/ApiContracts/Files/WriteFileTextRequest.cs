namespace Oscova.Agents.Core.ApiContracts.Files;

/// <summary>Carries the target path and text content to write to a file.</summary>
public sealed class WriteFileTextRequest
{
    public string Path { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Encrypt { get; set; }
}
