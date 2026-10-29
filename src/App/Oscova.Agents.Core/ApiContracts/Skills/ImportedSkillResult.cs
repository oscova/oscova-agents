namespace Oscova.Agents.Core.ApiContracts.Skills;

/// <summary>
/// Returns the imported skill metadata and any non-fatal compatibility warnings.
/// </summary>
public sealed class ImportedSkillResult
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IReadOnlyList<string> Warnings { get; set; } = [];
}
