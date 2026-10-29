namespace Oscova.Agents.Core.ApiContracts.Agents;

/// <summary>Metadata for a skill available for assignment to an agent.</summary>
public sealed class SkillDefinition
{
    public string Name { get; set; } = string.Empty;
    public bool IsShared { get; set; }
    public bool IsBundled { get; set; }
    public bool CanEdit { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>Reports recipient copies changed and personal copies preserved by skill publication.</summary>
public sealed record SkillPublicationResult(int Changed, int Preserved);
