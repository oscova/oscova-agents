namespace Oscova.Agents.Core.ApiContracts.Files;

/// <summary>Specifies the parent path, name, and optional initial content for a new file.</summary>
public sealed class CreateFileRequest
{
    public string? Path { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional content to write into the file on creation. When <see langword="null"/>, an empty file is created.</summary>
    public string? Content { get; set; }
}
