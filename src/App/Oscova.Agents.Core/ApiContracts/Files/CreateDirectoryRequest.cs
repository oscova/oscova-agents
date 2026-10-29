namespace Oscova.Agents.Core.ApiContracts.Files;

/// <summary>Specifies the parent path and name for a new directory.</summary>
public sealed class CreateDirectoryRequest
{
    public string? Path { get; set; }
    public string Name { get; set; } = string.Empty;
}
