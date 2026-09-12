namespace Oscova.Agents.Skills;

/// <summary>
/// Minimal skill metadata used by settings screens to present assignable skills.
/// </summary>
public sealed class DiscoveredSkill
{
    /// <summary>
    /// Gets the skill name declared in frontmatter or derived from the skill folder.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the short skill description shown in assignment surfaces.
    /// </summary>
    public string Description { get; init; } = string.Empty;
}