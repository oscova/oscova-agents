using Oscova.Agents.Policies;

namespace Oscova.Agents.Core.ApiContracts.Files;

/// <summary>Describes the user-visible state of the Library search index.</summary>
public sealed class LibraryIndexView
{
    public string? EmbeddingProviderName { get; init; }
    public string? EmbeddingModelId { get; init; }
    public EgressClass? EgressClass { get; init; }
    public bool IsBlockedByLocalOnlyPolicy { get; init; }
    public bool IsEmbeddingProviderAvailable { get; init; }
    public string? UnavailableReason { get; init; }
    public IReadOnlyList<LibraryFileIndexItemView> Files { get; init; } = [];
}
