using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Infrastructure.Memory;

/// <summary>Supplies pending conversation evidence for the agent's private behavioral reflection.</summary>
public sealed class ReflectionTaskHandler(UserServiceManager userServiceManager) : IAgentTaskHandler
{
    /// <inheritdoc />
    public async Task<AgentRunPlan?> PrepareAsync(AgentTaskDefinition task, CancellationToken ct = default)
    {
        var scope = await userServiceManager.GetOrCreateScopeAsync(task.UserId, ct).ConfigureAwait(false);
        return await ConversationEvidence.PrepareAsync(task, scope, reflection: true, ct).ConfigureAwait(false);
    }
}
