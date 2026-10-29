using Oscova.Agents.Core.ApiContracts.Files;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Core.Library;

/// <summary>Owns one user's rebuildable Library search index.</summary>
public interface ILibraryIndexingService : IAsyncDisposable
{
    /// <summary>Queues Library reconciliation and optionally marks the changed Library-relative path as queued.</summary>
    ValueTask QueueReconciliationAsync(
        CancellationToken cancellationToken = default,
        string? changedRelativePath = null);

    /// <summary>Gets provider and per-file Library index state.</summary>
    Task<LibraryIndexView> GetSnapshotAsync(
        bool requireLocalEndpoint = false,
        CancellationToken cancellationToken = default);

    /// <summary>Searches indexed Library chunks after validating the current workspace grant.</summary>
    Task<SemanticSearchResult<LibrarySearchResult>> SearchAsync(
        string userId,
        string query,
        int maxResults,
        bool requireLocalEndpoint,
        IReadOnlyList<AllowedDirectory> allowedDirectories,
        CancellationToken cancellationToken = default);

    /// <summary>Gets agent-visible Library indexing status after validating the current workspace grant.</summary>
    Task<LibraryIndexStatus> GetIndexStatusAsync(
        string userId,
        bool requireLocalEndpoint,
        IReadOnlyList<AllowedDirectory> allowedDirectories,
        CancellationToken cancellationToken = default);
}
