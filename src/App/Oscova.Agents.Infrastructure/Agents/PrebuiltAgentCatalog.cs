using Oscova.Agents.Core.ApiContracts.Agents;
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Infrastructure.Agents;

/// <summary>Projects active plugin declarations into the Host's starter catalog.</summary>
public sealed class PrebuiltAgentCatalog(IAgentPluginCatalog plugins)
{
    /// <summary>Returns qualified starters without creating user agents.</summary>
    public IReadOnlyList<PrebuiltAgentTemplate> GetAll() =>
    [
        .. plugins.Plugins.SelectMany(plugin => plugin.AgentTemplates.Select(template =>
            new PrebuiltAgentTemplate
            {
                Id = $"{plugin.Manifest.Id.ToLowerInvariant()}:{template.Id}",
                Name = template.Name,
                Description = template.Description,
                SystemInstruction = template.SystemInstruction,
                SetupInstructions = template.SetupInstructions,
                RequestedToolNames = template.RequestedToolNames,
                ProfilePictureKey = template.ProfilePictureKey
            }))
    ];
}
