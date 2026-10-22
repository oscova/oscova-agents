using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Oscova.Agents.Persistence;

/// <summary>
/// YAML frontmatter shape for an agent task definition stored in a task.md file.
/// </summary>
public sealed class AgentTaskDefinitionFile
{
    /// <summary>Identifies the task definition shape understood by this release.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets or sets the persisted task shape identifier.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>
    /// Gets or sets the persisted task identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the persisted task name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the opaque source assigned during provisioning.</summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? InstructionSource { get; set; }

    /// <summary>Gets or sets the saved instruction composition mode.</summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? InstructionMode { get; set; }

    /// <summary>Gets or sets whether execution requires a handler registered for the task directory.</summary>
    public bool RequiresHandler { get; set; }

    /// <summary>
    /// Gets or sets the agent identifier that executes the task.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional model override identifier.
    /// </summary>
    public string? ModelOverrideId { get; set; }

    /// <summary>
    /// Gets or sets the serialized task schedule block.
    /// </summary>
    public ScheduleFile Schedule { get; set; } = new();

    /// <summary>
    /// Gets or sets optional external trigger settings for task webhooks.
    /// </summary>
    public ExternalTriggerFile? ExternalTrigger { get; set; }

    /// <summary>
    /// Gets or sets whether the scheduler may run the task.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Gets or sets whether scheduled starts wait for the owner's foreground provider requests.</summary>
    public bool WaitForOwnerProviderIdle { get; set; } = true;

    /// <summary>Gets or sets whether a successful task run emits an automatic completion notice.</summary>
    public bool NotifyOnCompletion { get; set; }

    /// <summary>
    /// Gets or sets the serialized session mode.
    /// </summary>
    public string SessionMode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional token budget for the task.
    /// </summary>
    public int? MaxTokenBudget { get; set; }

    /// <summary>Gets or sets how many of the newest saved runs to keep, omitted to keep every run.</summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public int? KeepLastRuns { get; set; }

    /// <summary>
    /// Gets or sets the serialized tool approval run mode.
    /// </summary>
    public string ToolApprovalRunMode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets optional task-local runtime settings.
    /// </summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public TaskRunScopeFile? RunScope { get; set; }

    /// <summary>
    /// Gets or sets the ISO timestamp when the task was created.
    /// </summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the agent that created this task through a tool call.
    /// </summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? CreatedByAgentId { get; set; }
}

/// <summary>
/// YAML shape for task-local runtime settings stored inline in task.md frontmatter.
/// </summary>
public sealed class TaskRunScopeFile
{
    /// <summary>
    /// Gets or sets qualified tool names allowed for the task run.
    /// </summary>
    public IList<string> AllowedToolNames { get; set; } = [];

    /// <summary>
    /// Gets or sets sparse per-tool approval overrides for the task run.
    /// </summary>
    public IList<AgentToolPolicyFile> ToolPolicies { get; set; } = [];

    /// <summary>
    /// Gets or sets whether the task exposes tools through search.
    /// </summary>
    public bool IsToolSearchEnabled { get; set; }

    /// <summary>
    /// Gets or sets selected tools pinned outside search.
    /// </summary>
    public IList<string> PinnedToolNames { get; set; } = [];

    /// <summary>
    /// Gets or sets whether compaction is enabled for the task run.
    /// </summary>
    public bool IsCompactionEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether memory is enabled for the task run.
    /// </summary>
    public bool IsMemoryEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether todo context is enabled for the task run.
    /// </summary>
    public bool IsTodoEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether the task run may start same-agent subagents.
    /// </summary>
    public bool IsSubagentDelegationEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether the task run is local-only.
    /// </summary>
    public bool IsLocalOnly { get; set; }

    /// <summary>
    /// Gets or sets exposed agent identifiers available as tools.
    /// </summary>
    public IList<string> AgentToolIds { get; set; } = [];

    /// <summary>
    /// Gets or sets skill names available to the task run.
    /// </summary>
    public IList<string> AllowedSkillNames { get; set; } = [];

    /// <summary>
    /// Gets or sets workspace names available to the task run.
    /// </summary>
    public IList<string> AllowedWorkspaces { get; set; } = [];

    /// <summary>
    /// Gets or sets additional directory grants available to the task run.
    /// </summary>
    public IList<AllowedDirectoryFile> AllowedDirectories { get; set; } = [];
}

/// <summary>
/// YAML shape for the discriminated schedule block in task.md frontmatter.
/// </summary>
public sealed class ScheduleFile
{
    /// <summary>
    /// Gets or sets the serialized schedule kind.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the interval in minutes for interval schedules.
    /// </summary>
    public int? IntervalMinutes { get; set; }

    /// <summary>
    /// Gets or sets the ISO timestamp for one-shot schedules.
    /// </summary>
    public string? RunAt { get; set; }

    /// <summary>
    /// Gets or sets the cron expression for cron schedules.
    /// </summary>
    public string? Cron { get; set; }

    /// <summary>
    /// Gets or sets the time-zone identifier for cron schedules.
    /// </summary>
    public string? Timezone { get; set; }

    /// <summary>
    /// Gets or sets whether a successful one-shot task should be deleted instead of disabled.
    /// </summary>
    public bool DeleteAfterRun { get; set; }

    /// <summary>
    /// Gets or sets the predecessor task id for after-task schedules.
    /// </summary>
    public string? PredecessorTaskId { get; set; }
}

/// <summary>
/// YAML shape for external task webhook trigger settings.
/// </summary>
public sealed class ExternalTriggerFile
{
    /// <summary>
    /// Gets or sets whether external callers may trigger the task.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the external trigger URL slug.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the allowed external trigger parameters.
    /// </summary>
    public IList<ExternalTriggerParameterFile> Parameters { get; set; } = [];
}

/// <summary>
/// YAML shape for one allowed external task trigger parameter.
/// </summary>
public sealed class ExternalTriggerParameterFile
{
    /// <summary>
    /// Gets or sets the parameter name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the serialized parameter kind.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether callers must provide this parameter.
    /// </summary>
    public bool IsRequired { get; set; }
}
