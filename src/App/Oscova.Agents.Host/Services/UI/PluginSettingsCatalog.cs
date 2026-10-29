using System.Reflection;
using Microsoft.AspNetCore.Components;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Plugins;
using Oscova.Agents.Host.Components.Settings;
using Oscova.Agents.Infrastructure.Speech;

namespace Oscova.Agents.Host.Services.UI;

/// <summary>Admits optional compiled panels at Host-defined settings destinations.</summary>
public sealed class PluginSettingsCatalog
{
    /// <summary>Pairs an admitted component with the parameter type its context must satisfy.</summary>
    public sealed record Entry(LoadedAgentPlugin Plugin, AgentSettingsContribution Panel, Type ContextType);
    public IReadOnlyList<Entry> Entries { get; }
    /// <summary>Validates ownership, destinations and component contracts from the active catalog.</summary>
    public PluginSettingsCatalog(IAgentPluginCatalog plugins, ExecutionEnvironmentCatalog environments,
        ILogger<PluginSettingsCatalog> logger, SpeechProviderOwnership speech, ModelProviderOwnership models)
    {
        var admitted = new List<Entry>();
        var panels = plugins.Plugins.SelectMany(plugin => plugin.SettingsPanels.Select(panel => (Plugin: plugin, Panel: panel))).ToArray();
        foreach (var entry in panels)
        {
            var panel = entry.Panel;
            try
            {
                if (string.IsNullOrEmpty(panel.Id) || panel.Id.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) || panels.Count(other => other.Plugin == entry.Plugin &&
                    string.Equals(other.Panel.Id, panel.Id, StringComparison.OrdinalIgnoreCase)) != 1)
                { throw new InvalidOperationException("The plugin settings panel ID is empty or duplicated."); }

                if (!Enum.IsDefined(panel.Access)) { throw new InvalidOperationException("The settings access scope is unknown."); }

                if (panel.SlotId != PluginSettingsSlots.Page && panels.Count(other => other.Plugin == entry.Plugin && other.Panel.SlotId == panel.SlotId &&
                    string.Equals(other.Panel.AssociationId, panel.AssociationId, StringComparison.OrdinalIgnoreCase)) != 1)
                { throw new InvalidOperationException("The settings destination has duplicate panels."); }

                if (panel.SlotId == PluginSettingsSlots.Page)
                {
                    if (panel.AssociationId is not null || panel.Page is null || string.IsNullOrWhiteSpace(panel.Page.Title))
                    { throw new InvalidOperationException("Independent pages require page metadata and a title, with no AssociationId."); }

                    if (!SettingsPageRegistry.Categories.Any(category => category.Key == panel.Page.CategoryId))
                    { throw new InvalidOperationException($"Unknown settings category '{panel.Page.CategoryId}'. Supported categories: {string.Join(", ", SettingsPageRegistry.Categories.Select(category => category.Key))}."); }
                }
                else
                {
                    if (panel.Page is not null || string.IsNullOrWhiteSpace(panel.AssociationId))
                    { throw new InvalidOperationException("Inline panels require AssociationId and cannot declare page metadata."); }

                    var owner = panel.SlotId switch
                    {
                        PluginSettingsSlots.ExecutionEnvironment => environments.Owner(panel.AssociationId),
                        PluginSettingsSlots.SpeechRecognition => speech.RecognitionOwner(panel.AssociationId),
                        PluginSettingsSlots.SpeechSynthesis => speech.SynthesisOwner(panel.AssociationId),
                        PluginSettingsSlots.ModelProvider => models.Find(panel.AssociationId),
                        _ => throw new InvalidOperationException($"Unknown settings slot '{panel.SlotId}'.")
                    };
                    if (owner != entry.Plugin) { throw new InvalidOperationException("The settings association is not owned by this plugin."); }
                }

                var type = panel.ComponentType;
                if (type.IsAbstract || type.ContainsGenericParameters || !typeof(IComponent).IsAssignableFrom(type))
                { throw new InvalidOperationException("The settings type must be a concrete compiled Blazor component."); }

                var context = type.GetProperty("Context", BindingFlags.Instance | BindingFlags.Public);
                if (context?.SetMethod?.IsPublic != true || !context.IsDefined(typeof(ParameterAttribute), inherit: true))
                { throw new InvalidOperationException("The settings component requires a public Context parameter."); }

                admitted.Add(new(entry.Plugin, panel, context.PropertyType));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Plugin {PluginId} panel {PanelId} was rejected.", entry.Plugin.Manifest.Id, panel.Id);
                plugins.ReportDiagnostic(new(AgentPluginLoadDiagnosticLevel.Warning, entry.Plugin.PluginDirectory,
                    $"Settings panel '{panel.Id}' was rejected: {ex.Message}"));
            }
        }

        Entries = admitted.OrderBy(entry => entry.Plugin.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Panel.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
