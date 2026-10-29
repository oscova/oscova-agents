namespace Oscova.Agents.Core.Interfaces;

/// <summary>
/// In-memory index of file-backed agent definitions discovered under app_data/users.
/// </summary>
public interface IAgentCatalog
{
    /// <summary>
    /// Resolves an agent id to the directory containing its agent.md file, or null when unknown.
    /// </summary>
    string? TryGetAgentDirectory(string agentId);

    /// <summary>
    /// Resolves an agent id to the user id that owns the on-disk agent directory.
    /// </summary>
    string? TryGetOwnerUserId(string agentId);

    /// <summary>
    /// Returns all cataloged agents marked as shared.
    /// </summary>
    IReadOnlyCollection<AgentCatalogEntry> GetSharedEntries();

    /// <summary>
    /// Returns all cataloged agents.
    /// </summary>
    IReadOnlyCollection<AgentCatalogEntry> GetAllEntries();

    /// <summary>
    /// Returns all cataloged agents physically owned by <paramref name="userId"/>.
    /// </summary>
    IReadOnlyCollection<AgentCatalogEntry> GetPersonalEntriesForUser(string userId);

    /// <summary>
    /// Rebuilds the catalog from the filesystem.
    /// </summary>
    Task ReloadAsync(CancellationToken ct = default);

    /// <summary>
    /// Rebuilds only the slice of the catalog owned by <paramref name="userId"/>, leaving other users' entries untouched.
    /// </summary>
    Task ReloadUserAsync(string userId, CancellationToken ct = default);
    /// <summary>Returns whether this owner has an unlocked, completely read catalog slice.</summary>
    bool IsOwnerFullyLoaded(string userId);
}

/// <summary>
/// Catalog metadata needed to locate and filter an agent without rereading every file.
/// </summary>
public sealed record AgentCatalogEntry(
    string AgentId,
    string Name,
    string OwnerUserId,
    string DirectoryPath,
    bool IsShared,
    bool IsMain,
    IReadOnlyCollection<string> AllowedUserIds);