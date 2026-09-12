using System;

namespace Oscova.Agents.Skills;

/// <summary>Signals that a skill cannot be changed through the agent-authored update path.</summary>
public sealed class SkillAuthorshipException(string skillName)
    : InvalidOperationException($"Skill '{skillName}' is not eligible for authored-skill replacement.")
{
    /// <summary>Gets the validated skill name.</summary>
    public string SkillName { get; } = skillName;
}
