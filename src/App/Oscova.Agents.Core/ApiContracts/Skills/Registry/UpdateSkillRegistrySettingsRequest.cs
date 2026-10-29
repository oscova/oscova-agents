using System.ComponentModel.DataAnnotations;
using Oscova.Agents.Core.Skills.Registry;

namespace Oscova.Agents.Core.ApiContracts.Skills.Registry;

/// <summary>
/// Replaces the admin-managed list of GitHub repositories that act as skill registries.
/// </summary>
public sealed class UpdateSkillRegistrySettingsRequest
{
    [EnumDataType(typeof(SkillRegistrySearchMethod))]
    public SkillRegistrySearchMethod SearchMethod { get; set; }
    public IList<SkillRegistrySource> Sources { get; set; } = [];
}
