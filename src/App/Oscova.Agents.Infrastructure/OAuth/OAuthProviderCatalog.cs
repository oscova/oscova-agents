using Microsoft.Extensions.Logging;
using Oscova.Agents.Credentials;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Infrastructure.OAuth;

/// <summary>Claims durable provider ownership before exposing active account contributions.</summary>
public sealed class OAuthProviderCatalog(IAgentPluginCatalog plugins, ILogger<OAuthProviderCatalog> logger)
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Claims active account providers before user services are initialized.</summary>
    public async Task InitializeAsync(ISystemCredentialService credentials, CancellationToken ct)
    {
        foreach (var plugin in plugins.Plugins)
        {
            foreach (var definition in plugin.OAuthProviders)
            {
                ct.ThrowIfCancellationRequested();
                if (_entries.ContainsKey(definition.Id) ||
                    !await credentials.TryClaimOAuthProviderAsync(definition.Id, plugin.Manifest.Id,
                        PluginCredentialKeys.Qualify(plugin.Manifest.Id, definition.AppCredential.Key), ct)
                        .ConfigureAwait(false))
                {
                    logger.LogWarning("OAuth provider {ProviderId} from plugin {PluginId} has another owner.",
                        definition.Id, plugin.Manifest.Id);
                    plugins.ReportDiagnostic(new(AgentPluginLoadDiagnosticLevel.Warning, plugin.PluginDirectory,
                        $"OAuth provider '{definition.Id}' is already owned by another plugin."));
                    continue;
                }

                _entries.Add(definition.Id, new Entry(definition, plugin.Manifest.Id));
            }
        }

        Providers = _entries.Values.OrderBy(entry => entry.Definition.DisplayName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Definition.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Gets providers in their stable display order.</summary>
    public IReadOnlyList<Entry> Providers { get; private set; } = [];

    /// <summary>Finds an active provider using the shared case-insensitive lookup.</summary>
    public Entry? Find(string provider) => _entries.GetValueOrDefault(provider.Trim());

    /// <summary>Resolves an active provider or rejects an unsupported identifier.</summary>
    public Entry Get(string provider) => Find(provider)
        ?? throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported OAuth provider.");

    /// <summary>Associates an accepted provider with the plugin allowed to request its tokens.</summary>
    public sealed record Entry(OAuthProviderDefinition Definition, string PluginId);
}
