using Oscova.Agents.Definitions;
using Oscova.Agents.Conversations;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Services;

/// <summary>
/// Resolves the active chat agent from the persisted thread owner or a new-chat request.
/// </summary>
public static class AgentSelectionResolver
{
    /// <summary>Uses the web chat selection for its owner and guests that opt into consultation overrides.</summary>
    public static string? ResolveModelProviderId(
        AgentDefinition owner, AgentDefinition recipient, string? chatProviderId, string? defaultProviderId) =>
        recipient.Id == owner.Id || recipient.UseCallerModelForConsultations
            ? chatProviderId ?? owner.ModelOverrideId ?? defaultProviderId
            : recipient.ModelOverrideId ?? defaultProviderId;

    /// <summary>Authorizes an explicit guest through current user access without falling back to the main agent.</summary>
    public static async Task<AgentDefinition> ResolveRecipientAsync(
        UserServiceScope scope, ChatThread thread, AgentDefinition owner, string? recipientId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(recipientId) || recipientId == owner.Id) { return owner; }

        if (thread.DisplayMessages.Count == 0)
        {
            throw new InvalidOperationException("Start the conversation before inviting another agent.");
        }

        var guest = await scope.AgentDefinitionStore.GetAgentAsync(scope.UserId, recipientId, ct).ConfigureAwait(false);
        if (guest is null || !scope.AppService.CustomAgentDefinitions.Any(agent => agent.Id == guest.Id))
        {
            throw new UnauthorizedAccessException("The selected agent is unavailable. Select an agent you can access.");
        }

        return guest;
    }

    /// <summary>Returns the requested agent before the first message and the persisted thread agent afterward.</summary>
    public static string? ResolveAgentId(
        string? requestedAgentId,
        string? threadAgentId,
        bool conversationStarted) =>
        conversationStarted && !string.IsNullOrWhiteSpace(threadAgentId)
            ? threadAgentId
            : string.IsNullOrWhiteSpace(requestedAgentId) ? threadAgentId : requestedAgentId;

    /// <summary>
    /// Returns the thread agent, falling back to the new-chat selection and then the main agent.
    /// </summary>
    public static AgentDefinition Resolve(
        AgentDefinition mainAgent,
        IReadOnlyList<AgentDefinition> customAgents,
        string? selectedAgentId,
        string? threadAgentId,
        bool conversationStarted)
    {
        var agentId = ResolveAgentId(selectedAgentId, threadAgentId, conversationStarted);
        if (string.IsNullOrWhiteSpace(agentId) || string.Equals(agentId, mainAgent.Id, StringComparison.Ordinal))
        {
            return mainAgent;
        }

        return customAgents.FirstOrDefault(agent => string.Equals(agent.Id, agentId, StringComparison.Ordinal)) ?? mainAgent;
    }
}
