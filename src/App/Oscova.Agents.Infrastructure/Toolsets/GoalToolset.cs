using System.ComponentModel;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Harness;
using Oscova.Agents.Infrastructure.Conversations;
using Oscova.Agents.Policies;
using Oscova.Agents.Providers;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>Lets the admitted goal worker inspect its objective and report a blocker.</summary>
[DisplayName("goal")]
public sealed class GoalToolset(ConversationGoalStore goals, string userId, ChatThread thread,
    string goalId, string runId, string agentId, ThreadRunGuard runs, RunContextAccessor context) : IAgentToolset
{
    /// <summary>Reads the objective bound to the current worker run.</summary>
    [AgentTool, DisplayName("get_goal"), ToolPolicy(ApprovalMode = ToolApprovalMode.AutoApprove)]
    [Description("""
        Read the current goal, status and work cycle allowance.
        Available only to this goal's active worker run.
        """)]
    public async Task<ChatGoal> GetGoal(CancellationToken ct = default)
    {
        RequireWorker();
        var goal = await goals.GetAsync(userId, thread, ct).ConfigureAwait(false);
        RequireGoal(goal);
        return goal!;
    }

    /// <summary>Stops continuation for a blocker or submits a completion claim for checking.</summary>
    [AgentTool, DisplayName("update_goal"), ToolPolicy(ApprovalMode = ToolApprovalMode.AutoApprove)]
    [Description("""
        Report a blocker or a completion claim that still requires checking.
        status must be blocked or complete.
        blocked requires a nonblank reason explaining what the user must resolve.
        blocked stops this goal's current run and further continuation.
        Available only to this goal's active worker run.
        """)]
    public async Task<string> UpdateGoal(
        [Description("blocked or complete.")] string status,
        [Description("Required nonblank actionable reason when status is blocked.")] string? reason = null,
        CancellationToken ct = default)
    {
        RequireWorker();
        if (status is not ("blocked" or "complete")) { throw new ToolFailureException("status must be blocked or complete."); }

        if (status == "blocked" && string.IsNullOrWhiteSpace(reason)) { throw new ToolFailureException("reason must explain what the user must resolve before work can continue."); }

        await goals.UpdateAsync(userId, thread, goal =>
        {
            RequireGoal(goal);
            return status == "blocked" ? goal!.WithStatus(ChatGoalStatus.Blocked, reason!.Trim()) : goal;
        }, ct).ConfigureAwait(false);
        if (status == "blocked")
        {
            // Persist the blocker before canceling only the execution that reported it.
            runs.CancelExact(userId, thread.Id, runId);
            return "Goal blocked. Resolve the blocker and resume the goal to continue.";
        }

        return "Completion claimed. The goal completion check will confirm whether the objective is achieved.";
    }

    private void RequireWorker()
    {
        // A subagent shares its parent identity but never controls the goal.
        if (DelegatedWorkState.CurrentWorker is not null || context.Current?.CurrentAgentId != agentId || context.Current.UserId != userId
            || runs.GetActiveRunId(userId, thread.Id) != runId)
        {
            throw new ToolFailureException("Goal tools require this goal's active worker run.");
        }
    }

    private void RequireGoal(ChatGoal? goal)
    {
        if (goal?.Id != goalId || goal.Status != ChatGoalStatus.Active || goal.RunId != runId)
        {
            throw new ToolFailureException("The bound goal is no longer active. Resume an existing goal or start a new goal in the conversation.");
        }
    }
}
