using System.Collections.Generic;

namespace Oscova.Agents.Policies;

/// <summary>
/// Reports stale tool references removed from an agent or task runtime surface.
/// </summary>
public sealed class ToolReferenceSanitizationResult
{
    /// <summary>
    /// Gets the selected tool names removed because they are no longer available.
    /// </summary>
    public IReadOnlyList<string> RemovedAllowedToolNames { get; init; } = [];

    /// <summary>
    /// Gets the pinned tool names removed because they no longer reference a selected tool.
    /// </summary>
    public IReadOnlyList<string> RemovedPinnedToolNames { get; init; } = [];

    /// <summary>
    /// Gets the tool policy names removed because they no longer reference a selected tool.
    /// </summary>
    public IReadOnlyList<string> RemovedPolicyToolNames { get; init; } = [];

    /// <summary>
    /// Gets the exposed-agent IDs removed because they are no longer available.
    /// </summary>
    public IReadOnlyList<string> RemovedAgentToolIds { get; init; } = [];

    /// <summary>
    /// Gets whether any stale tool reference was removed.
    /// </summary>
    public bool HasChanges =>
        RemovedAllowedToolNames.Count > 0 ||
        RemovedPinnedToolNames.Count > 0 ||
        RemovedPolicyToolNames.Count > 0 ||
        RemovedAgentToolIds.Count > 0;
}
