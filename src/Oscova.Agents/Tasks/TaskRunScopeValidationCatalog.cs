using System.Collections.Generic;

namespace Oscova.Agents.Tasks;

/// <summary>
/// Carries the live user-specific choices a task run scope may reference.
/// </summary>
public sealed class TaskRunScopeValidationCatalog
{
    /// <summary>
    /// Gets or sets the direct tool names available in the current runtime.
    /// </summary>
    public IReadOnlySet<string>? ToolNames { get; set; }

    /// <summary>
    /// Gets or sets exposed agent ids available as agent tools.
    /// </summary>
    public IReadOnlySet<string>? ExposedAgentIds { get; set; }

    /// <summary>
    /// Gets or sets skill names available to the user.
    /// </summary>
    public IReadOnlySet<string>? SkillNames { get; set; }

    /// <summary>
    /// Gets or sets workspace names available to the user.
    /// </summary>
    public IReadOnlySet<string>? WorkspaceNames { get; set; }
}
