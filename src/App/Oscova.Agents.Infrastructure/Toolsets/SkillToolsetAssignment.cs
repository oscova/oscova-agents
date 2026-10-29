using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Definitions;
using Oscova.Agents.Runtime;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>
/// Assigns a skill to the currently running agent after a skill write succeeds.
/// </summary>
internal static class SkillToolsetAssignment
{
    /// <summary>
    /// Ensures the calling agent can load the skill on the next agent build.
    /// </summary>
    internal static async Task<SkillAssignmentResult> AssignToCurrentAgentAsync(
        string userId,
        string skillName,
        IAgentDefinitionRepository agentDefinitions,
        IAgentCacheInvalidator cacheInvalidator,
        RunContextAccessor runContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var context = runContext.Current;
        if (context is null || string.IsNullOrWhiteSpace(context.CurrentAgentId))
        {
            return SkillAssignmentResult.Failed("No active agent context; cannot assign the skill.");
        }

        var agent = await agentDefinitions
            .GetAgentAsync(userId, context.CurrentAgentId, cancellationToken)
            .ConfigureAwait(false);
        if (agent is null)
        {
            return SkillAssignmentResult.Failed("Could not resolve the calling agent to assign the skill.");
        }

        if (!agent.AllowedSkillNames.Contains(skillName, StringComparer.OrdinalIgnoreCase))
        {
            agent.AllowedSkillNames.Add(skillName);

            try
            {
                await agentDefinitions.SaveAgentAsync(userId, agent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
            {
                logger.LogWarning(ex, "Could not assign skill {SkillName} to agent {AgentId}.", skillName, agent.Id);
                return SkillAssignmentResult.Failed("The skill was saved, but this agent cannot be updated.");
            }
        }

        // Rebuild on the next run so AgentSkillsProvider sees the updated skill list and files.
        cacheInvalidator.InvalidateAllAgents();
        return SkillAssignmentResult.Succeeded(agent.Name);
    }
}

/// <summary>
/// Reports whether a skill could be assigned to the calling agent.
/// </summary>
internal sealed record SkillAssignmentResult(bool Success, string? AgentName, string? Message)
{
    internal static SkillAssignmentResult Succeeded(string agentName) => new(true, agentName, null);
    internal static SkillAssignmentResult Failed(string message) => new(false, null, message);
}
