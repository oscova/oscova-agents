using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Interfaces;

namespace Oscova.Agents.Core.Conversation;

/// <summary>Revalidates operator authority and resolves the admitted conversation shell override.</summary>
public static class ConversationExecutionAccess
{
    /// <summary>Rejects Full access whenever current operator authority is unavailable.</summary>
    public static async Task RequireAsync(IUserPermissionService permissions, string userId,
        ChatPermissionPreset preset, CancellationToken ct)
    {
        if (preset == ChatPermissionPreset.FullAccess &&
            !await permissions.IsOperatorAsync(userId, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("Full access is available only to the current operator. Select another chat permission preset.");
        }
    }

    /// <summary>Preserves Disabled and otherwise chooses Host only for explicit Full access.</summary>
    public static string? ResolveProviderId(ChatPermissionPreset preset, string? configured) =>
        preset == ChatPermissionPreset.FullAccess && configured != null
            ? "host" : configured;
}
