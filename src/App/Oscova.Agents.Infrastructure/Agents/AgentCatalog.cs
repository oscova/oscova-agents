using Microsoft.Extensions.Logging;
using Oscova.Agents.Core;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Persistence;
using YamlDotNet.Core;

namespace Oscova.Agents.Infrastructure.Agents;

/// <summary>
/// Scans app_data/users for agent.md files and keeps a lightweight lookup table in memory.
/// </summary>
public sealed class AgentCatalog(ILogger<AgentCatalog> logger, Oscova.Agents.Secrets.IContentProtector protector) : IAgentCatalog
{
    private const string AgentFileName = UserFileNames.AgentFile;
    private readonly YamlConverter _yamlCodec = new();
    private readonly Lock _writeLock = new();

    private AgentCatalogState _state = AgentCatalogState.Empty;

    /// <inheritdoc />
    public string? TryGetAgentDirectory(string agentId)
    {
        var state = _state;
        return state.EntriesByAgentId.TryGetValue(agentId, out var entry) ? entry.DirectoryPath : null;
    }

    /// <inheritdoc />
    public string? TryGetOwnerUserId(string agentId)
    {
        var state = _state;
        return state.EntriesByAgentId.TryGetValue(agentId, out var entry) ? entry.OwnerUserId : null;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AgentCatalogEntry> GetSharedEntries()
    {
        return _state.SharedEntries;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AgentCatalogEntry> GetAllEntries()
    {
        return _state.EntriesByAgentId.Values;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AgentCatalogEntry> GetPersonalEntriesForUser(string userId)
    {
        var state = _state;
        return state.EntriesByOwnerUserId.TryGetValue(userId, out var entries) ? entries : [];
    }

    /// <inheritdoc />
    public Task ReloadAsync(CancellationToken ct = default)
    {
        var fresh = AgentCatalogState.Empty;

        if (Directory.Exists(PathUtil.UsersPath))
        {
            foreach (var userDirectoryPath in Directory.EnumerateDirectories(PathUtil.UsersPath))
            {
                ct.ThrowIfCancellationRequested();

                var ownerUserId = Path.GetFileName(userDirectoryPath);
                if (!Guid.TryParse(ownerUserId, out _))
                {
                    logger.LogWarning(
                        "Skipping agent catalog user directory '{UserDirectoryPath}' because the user id segment is not a GUID.",
                        userDirectoryPath);
                    continue;
                }

                var entries = ScanUserAgents(userDirectoryPath, ownerUserId, ct, out var fullyLoaded);
                fresh = MergeUserSlice(fresh, ownerUserId, entries, fullyLoaded);
            }
        }

        lock (_writeLock)
        {
            _state = fresh;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReloadUserAsync(string userId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(userId, out _))
        {
            return Task.CompletedTask;
        }

        var userDirectoryPath = Path.Combine(PathUtil.UsersPath, userId);
        var freshUserEntries = ScanUserAgents(userDirectoryPath, userId, ct, out var fullyLoaded);

        lock (_writeLock)
        {
            _state = MergeUserSlice(_state, userId, freshUserEntries, fullyLoaded);
        }

        return Task.CompletedTask;
    }

    // Returns a new state with userId's slice replaced. Pure: never mutates the input state.
    private AgentCatalogState MergeUserSlice(AgentCatalogState current, string userId, List<AgentCatalogEntry> freshUserEntries, bool fullyLoaded)
    {
        var entriesByAgentId = new Dictionary<string, AgentCatalogEntry>(current.EntriesByAgentId, StringComparer.OrdinalIgnoreCase);
        var entriesByOwnerUserId = new Dictionary<string, List<AgentCatalogEntry>>(current.EntriesByOwnerUserId, StringComparer.OrdinalIgnoreCase);

        if (entriesByOwnerUserId.TryGetValue(userId, out var oldOwnerEntries))
        {
            foreach (var oldEntry in oldOwnerEntries)
            {
                entriesByAgentId.Remove(oldEntry.AgentId);
            }

            entriesByOwnerUserId.Remove(userId);
        }

        var loadedOwnerIds = new HashSet<string>(current.LoadedOwnerIds, StringComparer.Ordinal);
        if (fullyLoaded) { loadedOwnerIds.Add(userId); } else { loadedOwnerIds.Remove(userId); }

        var newOwnerEntries = new List<AgentCatalogEntry>();
        foreach (var entry in freshUserEntries)
        {
            if (!entriesByAgentId.TryAdd(entry.AgentId, entry))
            {
                loadedOwnerIds.Remove(userId);
                logger.LogWarning(
                    "Duplicate agent id '{AgentId}' at '{AgentDirectoryPath}'. Keeping the existing catalog entry.",
                    entry.AgentId,
                    entry.DirectoryPath);
                continue;
            }

            newOwnerEntries.Add(entry);
        }

        if (newOwnerEntries.Count > 0)
        {
            entriesByOwnerUserId[userId] = newOwnerEntries;
        }

        var sharedEntries = entriesByAgentId.Values.Where(entry => entry.IsShared).ToList();
        return new AgentCatalogState(entriesByAgentId, entriesByOwnerUserId, sharedEntries, loadedOwnerIds);
    }

    private List<AgentCatalogEntry> ScanUserAgents(string userDirectoryPath, string ownerUserId, CancellationToken ct, out bool fullyLoaded)
    {
        var entries = new List<AgentCatalogEntry>();
        fullyLoaded = protector.IsAvailable(ownerUserId);
        if (!fullyLoaded) { return entries; }

        var agentsRoot = Path.Combine(userDirectoryPath, UserFileNames.AgentsDirectory);
        if (!Directory.Exists(agentsRoot))
        {
            return entries;
        }

        var seenAgentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mainAgentClaimed = false;

        foreach (var agentDirectoryPath in Directory.EnumerateDirectories(agentsRoot))
        {
            ct.ThrowIfCancellationRequested();

            var agentFilePath = Path.Combine(agentDirectoryPath, AgentFileName);
            if (!File.Exists(agentFilePath))
            {
                continue;
            }

            try
            {
                var fileText = Oscova.Agents.Secrets.EncryptedFileContent.ReadAllText(ownerUserId, agentFilePath, protector)
                    ?? throw new FileNotFoundException("Agent definition not found.", agentFilePath);
                var (yaml, _) = MarkdownFrontmatter.Split(fileText);
                _yamlCodec.RequireSchemaVersion(yaml, AgentDefinitionFile.CurrentSchemaVersion);
                var file = _yamlCodec.Deserialize<AgentDefinitionFile>(yaml);
                var agentId = file.Id;
                if (string.IsNullOrWhiteSpace(agentId))
                {
                    fullyLoaded = false;
                    continue;
                }

                if (file.IsMain && mainAgentClaimed)
                {
                    fullyLoaded = false;
                    logger.LogWarning(
                        "Multiple main agents were found for user '{OwnerUserId}'. Keeping the first catalog entry.",
                        ownerUserId);
                    continue;
                }

                if (!seenAgentIds.Add(agentId))
                {
                    fullyLoaded = false;
                    logger.LogWarning(
                        "Duplicate agent id '{AgentId}' within user '{OwnerUserId}' at '{AgentDirectoryPath}'. Keeping the first catalog entry.",
                        agentId,
                        ownerUserId,
                        agentDirectoryPath);
                    continue;
                }

                if (file.IsMain)
                {
                    mainAgentClaimed = true;
                }

                entries.Add(new AgentCatalogEntry(
                    agentId,
                    file.Name,
                    ownerUserId,
                    agentDirectoryPath,
                    file.IsShared,
                    file.IsMain,
                    file.AllowedUserIds.ToArray()));
            }
            catch (Exception exception) when (exception is not InvalidDataException || exception.InnerException is YamlException)
            {
                fullyLoaded = false;
                logger.LogWarning(
                    exception,
                    "Skipping agent file '{AgentFilePath}' because it could not be loaded.",
                    agentFilePath);
            }
        }

        return entries;
    }

    private sealed record AgentCatalogState(
        Dictionary<string, AgentCatalogEntry> EntriesByAgentId,
        Dictionary<string, List<AgentCatalogEntry>> EntriesByOwnerUserId,
        List<AgentCatalogEntry> SharedEntries,
        HashSet<string> LoadedOwnerIds)
    {
        public static AgentCatalogState Empty { get; } = new(
            new Dictionary<string, AgentCatalogEntry>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, List<AgentCatalogEntry>>(StringComparer.OrdinalIgnoreCase),
            [],
            new HashSet<string>(StringComparer.Ordinal));
    }
    public bool IsOwnerFullyLoaded(string userId) => _state.LoadedOwnerIds.Contains(userId) && protector.IsAvailable(userId);
}
