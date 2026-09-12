using System;
using System.Collections.Generic;
using System.Linq;

namespace Oscova.Agents.Policies;

/// <summary>
/// Removes stale tool identifiers from persisted agent and task runtime surfaces.
/// </summary>
public static class ToolReferenceSanitizer
{
    /// <summary>
    /// Removes tool references that are absent from the live catalog and returns the removed names.
    /// </summary>
    public static ToolReferenceSanitizationResult RemoveUnavailableTools(
        IList<string> allowedToolNames,
        IList<string> pinnedToolNames,
        IList<AgentToolPolicy> toolPolicies,
        IReadOnlySet<string>? availableToolNames,
        IList<string>? agentToolIds = null,
        IReadOnlySet<string>? availableAgentIds = null)
    {
        var removedAgentToolIds = agentToolIds is null
            ? []
            : RemoveUnavailableNames(agentToolIds, availableAgentIds);
        if (availableToolNames is null)
        {
            return new ToolReferenceSanitizationResult
            {
                RemovedAgentToolIds = removedAgentToolIds
            };
        }

        var removedAllowedToolNames = RemoveUnavailableNames(allowedToolNames, availableToolNames);
        var allowedToolNameSet = allowedToolNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedPinnedToolNames = RemoveUnavailableNames(pinnedToolNames, allowedToolNameSet);
        var removedPolicyToolNames = RemoveUnavailablePolicies(toolPolicies, allowedToolNameSet);

        return new ToolReferenceSanitizationResult
        {
            RemovedAllowedToolNames = removedAllowedToolNames,
            RemovedPinnedToolNames = removedPinnedToolNames,
            RemovedPolicyToolNames = removedPolicyToolNames,
            RemovedAgentToolIds = removedAgentToolIds
        };
    }

    /// <summary>
    /// Removes unavailable names from a persisted catalog-backed list and returns the removed values.
    /// </summary>
    public static IReadOnlyList<string> RemoveUnavailableNames(
        IList<string> names,
        IReadOnlySet<string>? availableNames)
    {
        if (availableNames is null)
        {
            return [];
        }

        var removed = new List<string>();
        for (var index = names.Count - 1; index >= 0; index--)
        {
            var name = names[index];
            var normalizedName = name?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(normalizedName) && availableNames.Contains(normalizedName))
            {
                names[index] = normalizedName;
                continue;
            }

            removed.Add(name ?? string.Empty);
            names.RemoveAt(index);
        }

        removed.Sort(StringComparer.OrdinalIgnoreCase);
        return removed;
    }

    private static List<string> RemoveUnavailablePolicies(
        IList<AgentToolPolicy> toolPolicies,
        IReadOnlySet<string> allowedToolNames)
    {
        var removed = new List<string>();
        for (var index = toolPolicies.Count - 1; index >= 0; index--)
        {
            var toolName = toolPolicies[index].ToolName;
            var normalizedToolName = toolName?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(normalizedToolName) && allowedToolNames.Contains(normalizedToolName))
            {
                toolPolicies[index].ToolName = normalizedToolName;
                continue;
            }

            removed.Add(toolName ?? string.Empty);
            toolPolicies.RemoveAt(index);
        }

        removed.Sort(StringComparer.OrdinalIgnoreCase);
        return removed;
    }
}
