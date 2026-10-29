using Oscova.Agents.Definitions;
using Oscova.Agents.Host.Constants;

namespace Oscova.Agents.Host.Services.UI;

/// <summary>
/// Resolves agent avatar metadata for notification rows without duplicating profile-picture storage logic.
/// </summary>
public sealed class AgentAvatarLookup(IAgentDefinitionRepository agents, ProfilePictureRefreshState refreshState)
{
    /// <summary>Returns avatar URL and initials for a visible agent, or null when the agent is unknown.</summary>
    public async Task<AgentAvatar?> TryResolveAsync(string userId, string agentId, CancellationToken ct = default)
    {
        var agent = await agents.GetAgentAsync(userId, agentId, ct).ConfigureAwait(false);
        if (agent is null)
        {
            return null;
        }

        var pictureUrl = $"/{ApiRoutes.ProfilePictures.AgentById(agent.Id)}?v={refreshState.Version}";
        return new AgentAvatar(pictureUrl, ComputeInitials(agent.Name));
    }

    private static string ComputeInitials(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? "A" : name.Trim()[..1].ToUpperInvariant();
    }
}

/// <summary>
/// Avatar data displayed in notification rows.
/// </summary>
public sealed record AgentAvatar(string PictureUrl, string Initials);
