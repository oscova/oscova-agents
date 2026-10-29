using System.Reflection;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Secrets.Credentials;
using Oscova.Agents.Credentials;
using Oscova.Agents.Infrastructure.OAuth;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Infrastructure.Secrets;

/// <summary>
/// Holds every code-declared credential slot. Built once at startup from
/// <see cref="CredentialSlotAttribute"/> scans plus explicit host-owned declarations.
/// </summary>
public sealed class CredentialTypeRegistry : ICredentialTypeRegistry
{
    private readonly Dictionary<string, CredentialDeclaration> _byKey;
    private readonly Dictionary<string, LoadedAgentPlugin> _owners = new(StringComparer.OrdinalIgnoreCase);

    private CredentialTypeRegistry(IReadOnlyList<CredentialDeclaration> declarations)
    {
        All = declarations;
        _byKey = declarations.ToDictionary(d => Normalize(d.Key), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CredentialDeclaration> All { get; }

    public CredentialDeclaration? Find(string credentialKey)
    {
        if (string.IsNullOrWhiteSpace(credentialKey))
        {
            return null;
        }

        return _byKey.GetValueOrDefault(Normalize(credentialKey));
    }

    public LoadedAgentPlugin? GetOwner(string storedKey) => _owners.GetValueOrDefault(Normalize(storedKey));

    /// <summary>
    /// Builds the registry from the supplied declaration types plus explicit host declarations.
    /// </summary>
    public static CredentialTypeRegistry Build(IEnumerable<Type> slotDeclaringTypes, IEnumerable<CredentialDeclaration>? explicitDeclarations = null)
    {
        var declarations = new List<CredentialDeclaration>();
        var declarationsByKey = new Dictionary<string, CredentialDeclaration>(StringComparer.OrdinalIgnoreCase);

        void AddSharedDeclaration(CredentialDeclaration declaration)
        {
            var normalizedKey = Normalize(declaration.Key);
            if (declarationsByKey.ContainsKey(normalizedKey))
            {
                // Multiple providers or toolsets can intentionally share one credential slot.
                return;
            }

            declarations.Add(declaration);
            declarationsByKey[normalizedKey] = declaration;
        }

        void AddHostDeclaration(CredentialDeclaration declaration)
        {
            var normalizedKey = Normalize(declaration.Key);
            if (declarationsByKey.TryGetValue(normalizedKey, out var existingDeclaration))
            {
                declarations.Remove(existingDeclaration);
            }

            declarations.Add(declaration);
            declarationsByKey[normalizedKey] = declaration;
        }

        foreach (var type in slotDeclaringTypes)
        {
            if (ReadAttribute(type) is { } declaration) { AddSharedDeclaration(declaration); }
        }

        foreach (var declaration in explicitDeclarations ?? [])
        {
            if (string.IsNullOrWhiteSpace(declaration.Key) || string.IsNullOrWhiteSpace(declaration.Label) ||
                declaration.Fields.Count == 0 || declaration.Fields.Any(field => string.IsNullOrWhiteSpace(field.Name)) ||
                declaration.Fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != declaration.Fields.Count)
            {
                throw new InvalidOperationException("Plugin credential declarations require a key, label, and distinct named fields.");
            }

            AddSharedDeclaration(declaration);
        }

        // Shared GitHub discovery credentials remain host-owned.
        AddHostDeclaration(new CredentialDeclaration
        {
            Key = GitHubSkillsCredential.Key,
            Label = "GitHub Skill Search",
            Description = "Optional GitHub personal access token used when discovering and installing skills from GitHub repositories. Raises the API rate limit from 60 to 5,000 requests per hour for the whole host.",
            Scope = CredentialScope.System,
            Fields = CredentialFieldSets.ApiKey("Personal Access Token")
        });

        return new CredentialTypeRegistry(declarations);
    }

    private static CredentialDeclaration? ReadAttribute(Type type)
    {
        var attribute = type.GetCustomAttribute<CredentialSlotAttribute>(inherit: false);
        if (attribute is null) { return null; }

        if (string.IsNullOrWhiteSpace(attribute.Key) || string.IsNullOrWhiteSpace(attribute.Label) ||
            string.IsNullOrWhiteSpace(attribute.Description))
        {
            throw new InvalidOperationException($"[CredentialSlot] on '{type.FullName}' must set Key, Label, and Description.");
        }

        return new CredentialDeclaration
        {
            Key = attribute.Key, Label = attribute.Label, Description = attribute.Description,
            Scope = CredentialScope.User, Fields = CredentialFieldSets.ApiKey("API Key", attribute.EnvVariable)
        };
    }

    private static CredentialDeclaration Qualify(LoadedAgentPlugin plugin, CredentialDeclaration declaration) => new()
    {
        Key = PluginCredentialKeys.Qualify(plugin.Manifest.Id, declaration.Key),
        Label = declaration.Label,
        LabelKey = declaration.LabelKey,
        Description = declaration.Description,
        DescriptionKey = declaration.DescriptionKey,
        Scope = declaration.Scope,
        Fields = declaration.Fields
    };

    internal CredentialTypeRegistry WithPlugins(IAgentPluginCatalog plugins, OAuthProviderCatalog accounts, ILogger logger)
    {
        var declarations = All.ToList();
        var owners = new Dictionary<string, LoadedAgentPlugin>(StringComparer.OrdinalIgnoreCase);
        var keys = declarations.Select(item => Normalize(item.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins.Plugins)
        {
            void Add(CredentialDeclaration declaration)
            {
                var stored = Qualify(plugin, declaration);
                if (keys.Add(stored.Key))
                {
                    declarations.Add(stored);
                    owners.Add(stored.Key, plugin);
                }
            }

            var accountKeys = plugin.OAuthProviders.Select(item => item.AppCredential.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var declaration in plugin.Credentials)
            {
                if (accountKeys.Contains(declaration.Key) && !accounts.Providers.Any(entry =>
                    string.Equals(entry.PluginId, plugin.Manifest.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.Definition.AppCredential.Key, declaration.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Add(declaration);
            }

            var types = plugin.Toolsets.Select(item => item.ToolsetType)
                .Concat(plugin.ModelProviders.Select(item => item.ProviderType))
                .Concat(plugin.SpeechRecognitionProviders.Select(item => item.ProviderType))
                .Concat(plugin.SpeechSynthesisProviders.Select(item => item.ProviderType));
            foreach (var type in types)
            {
                try
                {
                    if (ReadAttribute(type) is { } declaration) { Add(declaration); }
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogWarning(ex, "Plugin {PluginId} credential metadata on {Type} was rejected.",
                        plugin.Manifest.Id, type.FullName);
                    plugins.ReportDiagnostic(new(AgentPluginLoadDiagnosticLevel.Warning, plugin.PluginDirectory,
                        $"Credential metadata on '{type.FullName}' was rejected: {ex.Message}"));
                }
            }
        }

        var registry = new CredentialTypeRegistry(declarations);
        foreach (var entry in owners) { registry._owners.Add(entry.Key, entry.Value); }

        return registry;
    }

    private static string Normalize(string credentialKey) => credentialKey.Trim().ToLowerInvariant();
}
