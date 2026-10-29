using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.ToolGovernance;
using Oscova.Agents.Settings;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Users;

/// <summary>
/// Converts a user's plaintext record files from display names to ids once, at the first unlock after the upgrade,
/// then grants the main agent the shared folder once.
/// </summary>
/// <remarks>
/// Names can only be matched to ids from the user's decrypted catalogs, so the step waits for the vault key.
/// Each store rewrites its own file under its write gate, because runs may write as soon as the key is available.
/// </remarks>
public sealed class RecordFormatMigration(
    IAppSettingsRepository settingsRepository,
    ILoggerFactory loggerFactory,
    IAgentCatalog agentCatalog,
    IEgressAuditStore egressAuditStore,
    IMetricsEventStore metricsEventStore,
    INotificationInbox notificationInbox,
    IToolQuotaUsageStore toolQuotaUsageStore,
    IUserPermissionService permissions,
    ILogger<RecordFormatMigration> logger)
{
    private const int IdsOnlyVersion = 1;
    private const int SharedFolderGrantVersion = 2;
    private const int ModelProviderOptionsVersion = 3;

    /// <summary>Runs each pending step once; a failure is logged and retried at the next unlock.</summary>
    public async Task RunAsync(UserServiceScope scope, CancellationToken ct = default)
    {
        var settings = new RecordFormatSettings(settingsRepository, scope.UserId, loggerFactory);
        try
        {
            await settings.LoadAsync().ConfigureAwait(false);
            if (settings.Version < IdsOnlyVersion)
            {
                await ConvertNamesToIdsAsync(scope, ct).ConfigureAwait(false);
                settings.Version = IdsOnlyVersion;
                await settings.SaveAsync().ConfigureAwait(false);
            }

            if (settings.Version < SharedFolderGrantVersion)
            {
                // The shared folder permission still decides whether the main agent's grant applies.
                scope.AppService.MainAgentDefinition.IsSharedFolderEnabled = true;
                await scope.AppService.SaveMainAgentAsync().ConfigureAwait(false);
                settings.Version = SharedFolderGrantVersion;
                await settings.SaveAsync().ConfigureAwait(false);
            }

            if (settings.Version < ModelProviderOptionsVersion)
            {
                // The owning store rewrites only personal definitions and preserves encrypted storage.
                await scope.ModelProviderSettings.SaveAsync().ConfigureAwait(false);
                settings.Version = ModelProviderOptionsVersion;
                await settings.SaveAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Failing here would fail the unlock, so the step logs and stays pending for the next unlock.
            logger.LogError(ex, "Record format upgrade to version {Version} failed for user {UserId}.", settings.Version + 1, scope.UserId);
        }
    }

    private async Task ConvertNamesToIdsAsync(UserServiceScope scope, CancellationToken ct)
    {
        var names = NameIndex.Create(scope, agentCatalog);
        await egressAuditStore.RewriteLinesAsync(scope.UserId, line => RewriteLine(line, names.RewriteEgress), ct).ConfigureAwait(false);
        await metricsEventStore.RewriteLinesAsync(scope.UserId, line => RewriteLine(line, names.RewriteMetrics), ct).ConfigureAwait(false);
        await notificationInbox.RewriteForUserAsync(scope.UserId, names.RewriteNotification, ct).ConfigureAwait(false);
        await toolQuotaUsageStore.EncryptPlaintextAsync(scope.UserId, ct).ConfigureAwait(false);

        // Mirrored host warnings and errors can name other users' agents and providers, which only their catalogs could convert.
        if (await permissions.IsOperatorAsync(scope.UserId, ct).ConfigureAwait(false))
        {
            await notificationInbox.RemoveSystemAsync(NotificationKind.SystemWarning, ct).ConfigureAwait(false);
            await notificationInbox.RemoveSystemAsync(NotificationKind.SystemError, ct).ConfigureAwait(false);
        }
    }

    private static string RewriteLine(string line, Action<JsonObject> rewrite)
    {
        JsonObject? record;
        try
        {
            record = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return line;
        }

        if (record is null) { return line; }

        rewrite(record);
        return record.ToJsonString();
    }

    /// <summary>Builds a function that replaces each name in a text with its id.</summary>
    /// <remarks>One pass over the original text, so an inserted id is never matched again and an existing id is never split.</remarks>
    internal static Func<string, string> CreateNameReplacer(IEnumerable<(string Name, string Id)> names)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, id) in names.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)))
        {
            ids.TryAdd(name, id);
        }

        if (ids.Count == 0) { return text => text; }

        // An id matches itself first; longer names come first, so a name that contains another is replaced whole.
        var alternatives = ids.Keys.OrderByDescending(name => name.Length).Select(Regex.Escape).Prepend(UserLabelMap.GuidPatternText);
        var pattern = new Regex(string.Join('|', alternatives), RegexOptions.None, TimeSpan.FromSeconds(1));
        return text => pattern.Replace(text, match => ids.GetValueOrDefault(match.Value, match.Value));
    }

    /// <summary>Matches the display names earlier versions wrote to the user's current ids.</summary>
    private sealed class NameIndex
    {
        private readonly Dictionary<string, string?> _agentsByName;
        private readonly Dictionary<string, string?> _agentsByCallableName;
        private readonly IReadOnlyList<(string Prefix, string Id)> _mcpServerPrefixes;
        private readonly Func<string, string> _replaceNames;
        private readonly IReadOnlySet<string> _installedToolNames;

        private NameIndex(
            Dictionary<string, string?> agentsByName,
            Dictionary<string, string?> agentsByCallableName,
            IReadOnlyList<(string Prefix, string Id)> mcpServerPrefixes,
            Func<string, string> replaceNames,
            IReadOnlySet<string> installedToolNames)
        {
            _agentsByName = agentsByName;
            _agentsByCallableName = agentsByCallableName;
            _mcpServerPrefixes = mcpServerPrefixes;
            _replaceNames = replaceNames;
            _installedToolNames = installedToolNames;
        }

        public static NameIndex Create(UserServiceScope scope, IAgentCatalog agentCatalog)
        {
            var main = scope.AppService.MainAgentDefinition;
            var ownAgents = agentCatalog.GetPersonalEntriesForUser(scope.UserId)
                .Select(entry => (Id: entry.AgentId, entry.Name))
                .Append((Id: main.Id, main.Name))
                .DistinctBy(agent => agent.Id)
                .ToList();
            var sharedAgents = agentCatalog.GetSharedEntries().Select(entry => (Id: entry.AgentId, entry.Name)).ToList();

            // Own agents win over shared ones; a name that stays ambiguous maps to no agent.
            var agentsByName = new Dictionary<string, string?>(StringComparer.Ordinal);
            var agentsByCallableName = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var tier in new[] { ownAgents, sharedAgents })
            {
                AddTier(agentsByName, tier.Select(agent => (agent.Name, agent.Id)));
                AddTier(agentsByCallableName, tier.Select(agent => (AgentCallableName.Normalize(agent.Name), agent.Id)));
            }

            var mcpServers = scope.McpToolSettings.GetAvailable();
            var mcpServerPrefixes = mcpServers
                .Select(server => (Prefix: ToolNaming.NormalizeQualifier(server.Name) + ToolNaming.WireSeparator, Id: server.Id.ToString()))
                .Where(server => server.Prefix.Length > ToolNaming.WireSeparator.Length)
                .OrderByDescending(server => server.Prefix.Length)
                .ToList();

            var replaceNames = CreateNameReplacer(scope.ModelProviderSettings.ModelProviderConfigs
                .Select(config => (config.Name, Id: config.Id.ToString()))
                .Concat(mcpServers.Select(server => (server.Name, Id: server.Id.ToString()))));

            return new NameIndex(agentsByName, agentsByCallableName, mcpServerPrefixes, replaceNames, scope.CrewService.GetBuiltInToolNames());
        }

        public void RewriteEgress(JsonObject record)
        {
            MoveAgentName(record);
            record.Remove("ProviderName");
            record.Remove("ThreadTitle");
            record.Remove("TaskName");

            if (record["McpServerId"] is null && record["ToolName"]?.GetValue<string>() is { } toolName)
            {
                var (plainName, mcpServerId, _) = MatchTool(toolName);
                SetOrRemove(record, "ToolName", plainName);
                SetOrRemove(record, "McpServerId", mcpServerId);
            }
        }

        public void RewriteMetrics(JsonObject metricsEvent)
        {
            foreach (var usage in (metricsEvent["Usage"] as JsonArray ?? []).OfType<JsonObject>())
            {
                MoveAgentName(usage);
                usage.Remove("ProviderName");
            }

            foreach (var tool in (metricsEvent["Tools"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (tool["McpServerId"] is not null || tool["AgentId"] is not null || tool["ToolName"]?.GetValue<string>() is not { } toolName)
                {
                    continue;
                }

                // Tool rows keep their counts; a name that matches nothing becomes empty and reads as unknown.
                var (plainName, mcpServerId, agentId) = MatchTool(toolName);
                tool["ToolName"] = plainName ?? string.Empty;
                SetOrRemove(tool, "McpServerId", mcpServerId);
                SetOrRemove(tool, "AgentId", agentId);
            }
        }

        public NotificationEntry RewriteNotification(NotificationEntry entry) =>
            entry with { Title = _replaceNames(entry.Title), Body = _replaceNames(entry.Body) };

        private void MoveAgentName(JsonObject record)
        {
            var agentName = record["AgentName"]?.GetValue<string>();
            record.Remove("AgentName");
            if (string.IsNullOrEmpty(agentName) || record["AgentId"] is not null) { return; }

            record["AgentId"] = agentName is MetricsEventBuilder.IncognitoBucket or MetricsEventBuilder.BackgroundBucket or UsageLedger.PrimaryBucket
                ? agentName
                : _agentsByName.GetValueOrDefault(agentName) ?? RecordLabelKeys.DeletedAgent;
        }

        // An MCP tool splits into its server id and plain name, an agent called as a tool maps to its id,
        // an installed tool keeps its name, and anything else is dropped.
        private (string? PlainName, string? McpServerId, string? AgentId) MatchTool(string toolName)
        {
            foreach (var (prefix, id) in _mcpServerPrefixes)
            {
                if (toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && toolName.Length > prefix.Length)
                {
                    return (toolName[prefix.Length..], id, null);
                }
            }

            if (_agentsByCallableName.GetValueOrDefault(toolName) is { } agentId)
            {
                return (null, null, agentId);
            }

            return _installedToolNames.Contains(toolName) ? (toolName, null, null) : (null, null, null);
        }

        private static void AddTier(Dictionary<string, string?> target, IEnumerable<(string Name, string Id)> tier)
        {
            foreach (var group in tier.Where(agent => !string.IsNullOrEmpty(agent.Name)).GroupBy(agent => agent.Name, StringComparer.Ordinal))
            {
                target.TryAdd(group.Key, group.Select(agent => agent.Id).Distinct().Count() == 1 ? group.First().Id : null);
            }
        }

        private static void SetOrRemove(JsonObject record, string property, string? value)
        {
            if (value is null)
            {
                record.Remove(property);
            }
            else
            {
                record[property] = value;
            }
        }
    }
}
