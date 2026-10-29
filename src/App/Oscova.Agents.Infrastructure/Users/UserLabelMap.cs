using System.Text.RegularExpressions;
using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;

namespace Oscova.Agents.Infrastructure.Users;

/// <summary>
/// Resolves the ids that plaintext records hold to the reading user's current labels.
/// </summary>
/// <remarks>
/// Records keep ids because their files are plaintext metadata and display names are user content.
/// The map is built in memory from the unlocked user's catalogs, so resolving adds no server work.
/// </remarks>
public sealed partial class UserLabelMap
{
    private readonly Dictionary<string, string> _agents;
    private readonly Dictionary<string, string> _providers;
    private readonly Dictionary<string, string> _mcpServers;
    private readonly HashSet<string> _sharedProviderIds;
    private readonly IReadOnlySet<string> _installedToolNames;
    private readonly bool _isSystemView;

    private UserLabelMap(
        Dictionary<string, string> agents,
        Dictionary<string, string> providers,
        Dictionary<string, string> mcpServers,
        HashSet<string> sharedProviderIds,
        IReadOnlySet<string> installedToolNames,
        bool isSystemView)
    {
        _agents = agents;
        _providers = providers;
        _mcpServers = mcpServers;
        _sharedProviderIds = sharedProviderIds;
        _installedToolNames = installedToolNames;
        _isSystemView = isSystemView;
    }

    /// <summary>Builds the map from one unlocked user's agent, provider, MCP server and tool catalogs.</summary>
    public static UserLabelMap Create(UserServiceScope scope, IAgentCatalog agentCatalog)
    {
        var agents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in agentCatalog.GetSharedEntries().Concat(agentCatalog.GetPersonalEntriesForUser(scope.UserId)))
        {
            agents[entry.AgentId] = entry.Name;
        }

        var main = scope.AppService.MainAgentDefinition;
        agents[main.Id] = main.Name;

        var providerSettings = scope.ModelProviderSettings;
        var providers = providerSettings.ModelProviderConfigs
            .ToDictionary(config => config.Id.ToString(), config => config.Name, StringComparer.OrdinalIgnoreCase);

        // Shared providers are the operator's own; for the operator that is its whole catalog.
        var operatorProviders = providerSettings.UsesOperatorCatalog ? providerSettings.SharedProviders : providerSettings.OwnedProviders;
        var sharedProviderIds = operatorProviders.Select(config => config.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var mcpServers = scope.McpToolSettings.GetAvailable()
            .ToDictionary(server => server.Id.ToString(), server => server.Name, StringComparer.OrdinalIgnoreCase);

        return new UserLabelMap(agents, providers, mcpServers, sharedProviderIds, scope.CrewService.GetBuiltInToolNames(), isSystemView: false);
    }

    /// <summary>
    /// Returns the operator's view of this user's records: only operator-shared providers and installed tools keep names.
    /// </summary>
    public UserLabelMap ForSystemView() =>
        new(_agents, _providers, _mcpServers, _sharedProviderIds, _installedToolNames, isSystemView: true);

    /// <summary>Whether this map is the operator's view of another user's records.</summary>
    public bool IsSystemView => _isSystemView;

    /// <summary>Whether the provider belongs to the operator, so every user may see its label.</summary>
    public bool IsSharedProvider(string? providerId) =>
        providerId is not null && _sharedProviderIds.Contains(providerId);

    /// <summary>Resolves an agent id or a reserved agent bucket.</summary>
    public string Agent(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) { return "unknown"; }

        if (agentId is MetricsEventBuilder.IncognitoBucket or MetricsEventBuilder.BackgroundBucket or UsageLedger.PrimaryBucket)
        {
            return agentId;
        }

        if (_isSystemView) { return RecordLabelKeys.OtherUsersAgents; }

        return _agents.GetValueOrDefault(agentId, RecordLabelKeys.DeletedAgent);
    }

    /// <summary>Resolves a model provider id.</summary>
    public string Provider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) { return "unknown"; }

        if (_isSystemView && !IsSharedProvider(providerId)) { return RecordLabelKeys.UsersOwnProviders; }

        return _providers.GetValueOrDefault(providerId, RecordLabelKeys.DeletedProvider);
    }

    /// <summary>Resolves a model id, hiding models of users' own providers in the system view.</summary>
    public string Model(string? providerId, string? modelId)
    {
        if (_isSystemView && !IsSharedProvider(providerId)) { return RecordLabelKeys.UsersOwnModels; }

        return string.IsNullOrWhiteSpace(modelId) ? "unknown" : modelId;
    }

    /// <summary>Resolves an MCP server id.</summary>
    public string McpServer(string? serverId) =>
        string.IsNullOrWhiteSpace(serverId) ? "unknown" : _mcpServers.GetValueOrDefault(serverId, RecordLabelKeys.DeletedMcpServer);

    /// <summary>Resolves a recorded tool: an MCP tool, an agent called as a tool, or an installed tool name.</summary>
    public string Tool(string? toolName, string? mcpServerId, string? agentId)
    {
        if (!string.IsNullOrWhiteSpace(mcpServerId))
        {
            if (_isSystemView) { return RecordLabelKeys.UsersOwnTools; }

            var server = McpServer(mcpServerId);
            return server == RecordLabelKeys.DeletedMcpServer ? server : $"{server}: {toolName}";
        }

        if (string.IsNullOrWhiteSpace(toolName) && !string.IsNullOrWhiteSpace(agentId)) { return Agent(agentId); }

        if (_isSystemView && (toolName is null || !_installedToolNames.Contains(toolName))) { return RecordLabelKeys.UsersOwnTools; }

        return string.IsNullOrWhiteSpace(toolName) ? "unknown" : toolName;
    }

    /// <summary>Returns the agent, provider and MCP server ids whose current labels contain the term.</summary>
    public IReadOnlyCollection<string> FindIds(string? term)
    {
        if (string.IsNullOrWhiteSpace(term)) { return []; }

        var trimmed = term.Trim();
        return [.. _agents.Concat(_providers).Concat(_mcpServers)
            .Where(pair => pair.Value.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)];
    }

    /// <summary>Wraps a record with its resolved agent, provider and tool labels.</summary>
    public EgressAuditEntry Label(EgressAuditRecord record, string? ownerUserId = null, string? ownerDisplayName = null) => new()
    {
        Record = record,
        OwnerUserId = ownerUserId,
        OwnerDisplayName = ownerDisplayName,
        AgentLabel = record.AgentId is null ? null : Agent(record.AgentId),
        ProviderLabel = record.ProviderId is null ? null : Provider(record.ProviderId),
        ToolLabel = record.ToolName is null && record.McpServerId is null ? null : Tool(record.ToolName, record.McpServerId, null)
    };

    /// <summary>
    /// Replaces known provider, MCP server and agent ids in notification or error text with their labels.
    /// </summary>
    /// <remarks>Ids the user cannot resolve stay as text, so nothing is guessed.</remarks>
    public string ReplaceIds(string text) =>
        string.IsNullOrEmpty(text)
            ? text
            : GuidPattern().Replace(text, match =>
                _providers.GetValueOrDefault(match.Value)
                ?? _mcpServers.GetValueOrDefault(match.Value)
                ?? _agents.GetValueOrDefault(match.Value)
                ?? match.Value);

    internal const string GuidPatternText = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    [GeneratedRegex(GuidPatternText, RegexOptions.None, 1000)]
    private static partial Regex GuidPattern();
}
