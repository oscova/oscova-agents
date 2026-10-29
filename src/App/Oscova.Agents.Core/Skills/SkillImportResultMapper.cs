using Oscova.Agents.Core.ApiContracts.Skills;
using Oscova.Agents.Skills;

namespace Oscova.Agents.Core.Skills;

/// <summary>Maps framework skill-import facts into product API and tool wording.</summary>
public static class SkillImportResultMapper
{
    /// <summary>Creates the product import result and compatibility warning text.</summary>
    public static ImportedSkillResult ToImportedSkillResult(SkillImportResult result)
    {
        var warnings = new List<string>();

        if (result.UnusedTopLevelPaths.Count > 0)
        {
            warnings.Add($"Extra top-level paths are not used by this app: {string.Join(", ", result.UnusedTopLevelPaths)}.");
        }

        if (result.DeclaresAllowedTools)
        {
            warnings.Add("This app does not currently auto-apply SKILL.md allowed-tools. Make sure the target agent already allows the tools this skill needs.");
        }

        if (result.IgnoredFrontmatterKeys.Count > 0)
        {
            warnings.Add($"This app ignores these SKILL.md frontmatter keys: {string.Join(", ", result.IgnoredFrontmatterKeys)}.");
        }

        return new ImportedSkillResult
        {
            Name = result.Name,
            Description = result.Description,
            Warnings = warnings
        };
    }
}
