namespace Oscova.Agents.Core.Skills.Registry;

/// <summary>
/// A skill discovered in a remote GitHub registry, identified by the folder that owns its SKILL.md.
/// </summary>
public sealed class RemoteSkillCatalogEntry
{
    public required SkillRegistrySource Source { get; init; }
    public required string SkillName { get; init; }
    public required string SkillPath { get; init; }
    public string? Description { get; set; }
}

/// <summary>Identifies a skill file at the revision selected for installation.</summary>
public sealed record RemoteSkillFile(
    SkillRegistrySource Source, string RepoPath, long Size, string RevisionSha, string? BlobSha = null);
