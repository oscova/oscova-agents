using Oscova.Agents.Definitions;
using Oscova.Agents.Policies;
using Oscova.Agents.Workspace;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Oscova.Agents.Tasks;

/// <summary>
/// Overrides selected agent runtime settings for one task run without changing agent.md.
/// </summary>
public sealed class TaskRunScope
{
    /// <summary>
    /// Gets or sets the qualified tool names exposed to this task run.
    /// </summary>
    public IList<string> AllowedToolNames { get; set; } = [];

    /// <summary>
    /// Gets or sets sparse approval-policy overrides for this task run.
    /// </summary>
    public IList<AgentToolPolicy> ToolPolicies { get; set; } = [];

    /// <summary>
    /// Gets or sets whether selected tools are exposed through tool search.
    /// </summary>
    public bool IsToolSearchEnabled { get; set; }

    /// <summary>
    /// Gets or sets selected tools kept visible when tool search is enabled.
    /// </summary>
    public IList<string> PinnedToolNames { get; set; } = [];

    /// <summary>
    /// Gets or sets whether conversation compaction is enabled for this task run.
    /// </summary>
    public bool IsCompactionEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether user memory context and tools are available for this task run.
    /// </summary>
    public bool IsMemoryEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether todo context is available for this task run.
    /// </summary>
    public bool IsTodoEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether this task run may use only local endpoints.
    /// </summary>
    public bool IsLocalOnly { get; set; }

    /// <summary>
    /// Gets or sets exposed agent identifiers callable by this task run.
    /// </summary>
    public IList<string> AgentToolIds { get; set; } = [];

    /// <summary>
    /// Gets or sets skill names available to this task run.
    /// </summary>
    public IList<string> AllowedSkillNames { get; set; } = [];

    /// <summary>
    /// Gets or sets workspace names available to this task run.
    /// </summary>
    public IList<string> AllowedWorkspaces { get; set; } = [];

    /// <summary>
    /// Gets or sets additional folder grants available to this task run.
    /// </summary>
    public IList<AllowedDirectory> AllowedDirectories { get; set; } = [];

    /// <summary>
    /// Creates a task-scope snapshot from an agent's current runtime settings.
    /// </summary>
    public static TaskRunScope FromAgent(AgentDefinition agent)
    {
        var allowedToolNames = agent.AllowedToolNames
            .Where(toolName => !IsManagedToolName(toolName))
            .ToList();
        var allowedToolNameSet = allowedToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new TaskRunScope
        {
            AllowedToolNames = allowedToolNames,
            ToolPolicies = CloneToolPolicies(agent.ToolPolicies
                .Where(policy => allowedToolNameSet.Contains(policy.ToolName))),
            IsToolSearchEnabled = agent.IsToolSearchEnabled,
            PinnedToolNames = [.. agent.PinnedToolNames.Where(allowedToolNameSet.Contains)],
            IsCompactionEnabled = agent.IsCompactionEnabled,
            IsMemoryEnabled = agent.IsMemoryEnabled,
            IsTodoEnabled = agent.IsTodoEnabled,
            IsLocalOnly = agent.IsLocalOnly,
            AgentToolIds = [.. agent.AgentToolIds],
            AllowedSkillNames = [.. agent.AllowedSkillNames],
            AllowedWorkspaces = [.. agent.AllowedWorkspaces],
            AllowedDirectories = CloneDirectories(agent.AllowedDirectories)
        };
    }

    /// <summary>
    /// Returns whether a tool name is controlled by dedicated task-scope flags instead of allowed_tool_names.
    /// </summary>
    /// <summary>Identifies tools controlled exclusively by task-scope flags.</summary>
        public static bool IsManagedToolName(string? toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return false;
            }

            return string.Equals(toolName, "search_tools", StringComparison.OrdinalIgnoreCase)
                || toolName.StartsWith("library_", StringComparison.OrdinalIgnoreCase);
        }

    /// <summary>
    /// Creates a mutable deep copy so callers can edit a scope without mutating the source.
    /// </summary>
    public TaskRunScope Clone()
    {
        return new TaskRunScope
        {
            AllowedToolNames = [.. AllowedToolNames],
            ToolPolicies = CloneToolPolicies(ToolPolicies),
            IsToolSearchEnabled = IsToolSearchEnabled,
            PinnedToolNames = [.. PinnedToolNames],
            IsCompactionEnabled = IsCompactionEnabled,
            IsMemoryEnabled = IsMemoryEnabled,
            IsTodoEnabled = IsTodoEnabled,
            IsLocalOnly = IsLocalOnly,
            AgentToolIds = [.. AgentToolIds],
            AllowedSkillNames = [.. AllowedSkillNames],
            AllowedWorkspaces = [.. AllowedWorkspaces],
            AllowedDirectories = CloneDirectories(AllowedDirectories)
        };
    }

    private static List<AgentToolPolicy> CloneToolPolicies(IEnumerable<AgentToolPolicy> policies)
    {
        return [.. policies.Select(policy => new AgentToolPolicy
        {
            ToolName = policy.ToolName,
            ApprovalMode = policy.ApprovalMode
        })];
    }

    private static List<AllowedDirectory> CloneDirectories(IEnumerable<AllowedDirectory> directories)
    {
        return [.. directories.Select(directory => directory with { })];
    }
}
