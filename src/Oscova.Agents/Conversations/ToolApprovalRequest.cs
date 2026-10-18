using System.Text.Json;

namespace Oscova.Agents.Conversations;

/// <summary>Describes a tool call awaiting user approval.</summary>
public sealed class ToolApprovalRequest
{
    /// <summary>Gets or sets the approval request identifier.</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the captured identity supports a reusable approval.</summary>
    public bool CanAllowForSession { get; set; }

    /// <summary>Gets or sets the original tool-call identifier.</summary>
    public string? ToolCallId { get; set; }

    /// <summary>Gets or sets the selected execution machine.</summary>
    public string? ExecutionDevice { get; set; }

    /// <summary>Gets or sets the effective execution target.</summary>
    public string? ExecutionTarget { get; set; }

    /// <summary>Gets or sets a resolved outcome that never restores execution authority.</summary>
    public ToolApprovalOutcome? Outcome { get; set; }

    /// <summary>Gets or sets the requested tool name.</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>Gets or sets the identity of the agent requesting this decision.</summary>
    public string? AgentId { get; set; }

    /// <summary>Gets or sets the display name of the agent requesting this decision.</summary>
    public string? AgentName { get; set; }

    /// <summary>Gets or sets the approval message shown to the user.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the serialized tool arguments.</summary>
    public JsonElement? Arguments { get; set; }

    /// <summary>Gets or sets optional structured review details.</summary>
    public ToolApprovalReview? Review { get; set; }
}

/// <summary>Carries structured display data for a tool approval decision.</summary>
public sealed class ToolApprovalReview
{
    /// <summary>Gets or sets the review kind.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the requested operation.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>Gets or sets the related skill name.</summary>
    public string? SkillName { get; set; }

    /// <summary>Gets or sets the operation description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the related task name.</summary>
    public string? TaskName { get; set; }

    /// <summary>Gets or sets the proposed new task name.</summary>
    public string? NewTaskName { get; set; }

    /// <summary>Gets or sets the related task agent name.</summary>
    public string? TaskAgentName { get; set; }

    /// <summary>Gets or sets the schedule kind.</summary>
    public string? ScheduleKind { get; set; }

    /// <summary>Gets or sets the schedule value.</summary>
    public string? ScheduleValue { get; set; }

    /// <summary>Gets or sets the schedule time-zone identifier.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>Gets or sets the requested run mode.</summary>
    public string? RunMode { get; set; }

    /// <summary>Gets or sets the registry server name being installed.</summary>
    public string? ServerName { get; set; }

    /// <summary>Gets or sets the publisher of the installed item.</summary>
    public string? Publisher { get; set; }

    /// <summary>Gets or sets the installed item version.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the source repository URL of the installed item.</summary>
    public string? SourceRepository { get; set; }

    /// <summary>Gets or sets the connection transport of the installed server.</summary>
    public string? Transport { get; set; }

    /// <summary>Gets or sets the command line or endpoint the installed server runs.</summary>
    public string? Command { get; set; }

    /// <summary>Gets or sets the declared network scope of the installed server.</summary>
    public string? NetworkScope { get; set; }
}
