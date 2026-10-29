using Oscova.Agents.Core.Library;

namespace Oscova.Agents.Core.ApiContracts.Files;

/// <summary>Exposes safe indexing metadata for one file in Library.</summary>
public sealed class LibraryFileIndexItemView
{
    public string Path { get; init; } = string.Empty;
    public LibraryFileIndexState State { get; init; }
    public string? Reason { get; init; }
}
