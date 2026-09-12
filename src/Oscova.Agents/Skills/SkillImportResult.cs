using System.Collections.Generic;

namespace Oscova.Agents.Skills;

/// <summary>Describes an installed skill and compatibility facts discovered during import.</summary>
public sealed class SkillImportResult
{
    /// <summary>Gets the validated skill name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the validated skill description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets top-level paths that runtime discovery does not use.</summary>
    public IReadOnlyList<string> UnusedTopLevelPaths { get; init; } = [];

    /// <summary>Gets whether the skill declares the allowed-tools frontmatter key.</summary>
    public bool DeclaresAllowedTools { get; init; }

    /// <summary>Gets recognized frontmatter keys that runtime discovery ignores.</summary>
    public IReadOnlyList<string> IgnoredFrontmatterKeys { get; init; } = [];
}
