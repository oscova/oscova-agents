using System;
using Oscova.Agents.Policies;
using System.Text.Json;

namespace Oscova.Agents.Tasks
{
    /// <summary>
    /// Describes a saved unit of agent work that can run manually or on a schedule.
    /// </summary>
    public class AgentTaskDefinition
    {
        /// <summary>
        /// Gets or sets the stable task identifier.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Gets or sets the user-facing task name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets whether execution requires a handler registered for the task directory.</summary>
        public bool RequiresHandler { get; set; }

        /// <summary>
        /// Gets or sets the instruction submitted when the task executes.
        /// </summary>
        public string Instruction { get; set; } = string.Empty;

        /// <summary>Gets or sets the opaque source assigned during task provisioning.</summary>
        public string? InstructionSource { get; set; }

        /// <summary>Gets or sets the saved instruction composition mode.</summary>
        public string? InstructionMode { get; set; }

        /// <summary>
        /// Gets or sets the agent identifier that executes the task.
        /// </summary>
        public string AgentId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the user that owns the task.
        /// </summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets an optional model override used for this task run.
        /// </summary>
        public string? ModelOverrideId { get; set; }

        /// <summary>
        /// Gets or sets the schedule that determines automatic task execution.
        /// </summary>
        public TaskSchedule Schedule { get; set; } = new();

        /// <summary>
        /// Gets or sets the optional externally callable webhook trigger settings.
        /// </summary>
        public TaskExternalTrigger ExternalTrigger { get; set; } = new();

        /// <summary>
        /// Gets or sets whether the scheduler may run this task.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>Gets or sets whether scheduled starts wait for the owner's foreground provider requests to finish.</summary>
        public bool WaitForOwnerProviderIdle { get; set; } = true;

        /// <summary>Gets or sets whether a successful task run emits an automatic completion notice.</summary>
        public bool NotifyOnCompletion { get; set; }

        /// <summary>
        /// Gets or sets whether task runs use an isolated or persistent session.
        /// </summary>
        public TaskSessionMode SessionMode { get; set; } = TaskSessionMode.Isolated;

        /// <summary>
        /// Gets or sets an optional token budget for the task run.
        /// </summary>
        public int? MaxTokenBudget { get; set; }

        /// <summary>Gets or sets how many of the newest saved runs to keep, or null to keep every run.</summary>
        public int? KeepLastRuns { get; set; }

        /// <summary>
        /// Gets or sets how approval-required tools are handled during unattended execution.
        /// </summary>
        public ToolApprovalRunMode ToolApprovalRunMode { get; set; } = ToolApprovalRunMode.AutoRejectRequired;

        /// <summary>
        /// Gets or sets optional task-local runtime settings for the selected agent.
        /// </summary>
        public TaskRunScope? RunScope { get; set; }

        /// <summary>
        /// Gets or sets when the task definition was created.
        /// </summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Gets or sets the agent that created this task through a tool call, or null when user-created.
        /// </summary>
        public string? CreatedByAgentId { get; set; }

        /// <summary>Creates a run-local view with the selected instruction without changing the stored snapshot.</summary>
        public AgentTaskDefinition WithInstruction(string instruction)
        {
            var copy = (AgentTaskDefinition)MemberwiseClone();
            copy.Instruction = instruction;
            return copy;
        }

        /// <summary>Returns whether another snapshot has the same serializable task definition.</summary>
        public bool HasSameContentAs(AgentTaskDefinition other)
        {
            var current = GetContentFingerprint();
            var candidate = other.GetContentFingerprint();
            return current.AsSpan().SequenceEqual(candidate);
        }

        /// <summary>Serializes the definition for optimistic concurrency and stale-admission checks.</summary>
        public byte[] GetContentFingerprint() => JsonSerializer.SerializeToUtf8Bytes(this);
    }
}
