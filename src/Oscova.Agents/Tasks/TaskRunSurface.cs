using System.Collections.Generic;
using Oscova.Agents.Definitions;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tasks;

/// <summary>
/// Resolved one-run view of an agent; it is runtime-only and must not be saved back to agent.md.
/// </summary>
public sealed class TaskRunSurface
{
    /// <summary>
    /// Gets the effective agent definition used for this run.
    /// </summary>
    public required AgentDefinition EffectiveAgent { get; init; }

    /// <summary>
    /// Gets the resolved runtime directories used for prompts, shell mounts, and path validation.
    /// </summary>
    public IReadOnlyList<AllowedDirectory> Directories { get; init; } = [];

    /// <summary>
    /// Gets whether a task-local scope replaced the selected agent's normal runtime settings.
    /// </summary>
    public bool IsScopeApplied => NormalizedRunScope is not null;

    /// <summary>
    /// Gets the normalized task scope that should be persisted when a scope was applied.
    /// </summary>
    public TaskRunScope? NormalizedRunScope { get; init; }
}
