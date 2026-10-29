using Oscova.Agents.Core.Skills.Registry;

namespace Oscova.Agents.Core.ApiContracts.Skills.Registry;

/// <summary>
/// All remote skills discovered across the configured registries, plus any per-source errors.
/// </summary>
public sealed class SkillRegistryCatalogResponse
{
    public IList<RemoteSkillCatalogEntry> Entries { get; set; } = [];
    public IList<string> Errors { get; set; } = [];
}
