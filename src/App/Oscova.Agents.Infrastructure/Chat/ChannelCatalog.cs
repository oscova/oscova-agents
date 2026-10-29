using Microsoft.Extensions.Logging;
using Oscova.Agents.Channels;
using Oscova.Agents.Core.Channels;
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Validates loaded channel declarations before exposing them to private product consumers.</summary>
public sealed class ChannelCatalog : IChannelCatalog
{
    private readonly Dictionary<string, (LoadedAgentPlugin Plugin, AgentChannelContribution Contribution)> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the channel catalog from enabled plugins and rejects conflicting declarations.</summary>
    public ChannelCatalog(IAgentPluginCatalog plugins, ILogger<ChannelCatalog> logger)
    {
        foreach (var plugin in plugins.Plugins)
        {
            foreach (var contribution in plugin.Channels)
            {
                var definition = contribution.Definition;
                if (_entries.ContainsKey(definition.Id) ||
                    definition.CredentialKeys.Any(key => !plugin.DeclaredCredentialKeys.Contains(key)))
                {
                    logger.LogError("Channel {ChannelId} from plugin {PluginId} has invalid or conflicting declarations.",
                        definition.Id, plugin.Manifest.Id);
                    plugins.ReportDiagnostic(new(AgentPluginLoadDiagnosticLevel.Warning, plugin.PluginDirectory,
                        $"Channel '{definition.Id}' has conflicting channel or credential ownership."));
                    continue;
                }

                _entries.Add(definition.Id, (plugin, contribution));
            }
        }

        Channels = _entries.Values.Select(entry => entry.Contribution.Definition).ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<ChannelDefinition> Channels { get; }

    /// <summary>Resolves a validated contribution together with its declaring plugin.</summary>
    public (LoadedAgentPlugin Plugin, AgentChannelContribution Contribution) Get(string channelId) =>
        _entries.TryGetValue(channelId, out var entry)
            ? entry : throw new KeyNotFoundException("The channel is unavailable. Enable its plugin and restart.");
}
