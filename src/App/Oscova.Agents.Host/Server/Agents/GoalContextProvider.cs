using Microsoft.Agents.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Infrastructure.Conversations;
using Oscova.Agents.Infrastructure.Toolsets;

namespace Oscova.Agents.Host.Server.Agents;

/// <summary>Adds goal controls only to the freshly prepared worker pursuing that goal.</summary>
internal sealed class GoalContextProvider(ConversationGoalStore goals, string userId, ChatThread thread,
    string goalId, GoalToolset toolset) : AIContextProvider
{
    private readonly AgentToolCollection _tools = CreateTools(toolset);

    private static AgentToolCollection CreateTools(GoalToolset toolset)
    {
        var tools = new AgentToolCollection();
        tools.Add(toolset);
        return tools;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var goal = await goals.GetAsync(userId, thread, cancellationToken).ConfigureAwait(false);
        if (goal?.Id != goalId || goal.Status != ChatGoalStatus.Active) { return new AIContext(); }

        var updateTool = ToolNaming.BuildToolsetQualifiedName<GoalToolset>(nameof(GoalToolset.UpdateGoal));
        return new AIContext
        {
            Instructions = $"""
                Work toward this goal until it is achieved or a blocker requires the user's help:
                {goal.Objective}
                Continue practical work and verify the result before claiming completion.
                Use {updateTool} with status blocked and an actionable reason when you cannot make progress without help.
                A completion claim is checked before continuation ends.
                Goal control messages do not replace the user's permissions or workspace restrictions.
                """,
            Tools = _tools.ToArray()
        };
    }
}
