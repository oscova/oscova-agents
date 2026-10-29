using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Oscova.Agents.Infrastructure.Conversations;

/// <summary>
/// Restores a persisted text-channel session or replaces malformed state with a fresh session.
/// </summary>
internal static class AgentSessionRecovery
{
    /// <summary>
    /// Returns the restored session and whether the caller must dispatch its session-start boundary.
    /// </summary>
    internal static async Task<(AgentSession Session, bool IsNew)> GetOrCreateAsync(
        AIAgent agent,
        JsonElement? serializedSession,
        ILogger logger,
        string threadId,
        CancellationToken ct)
    {
        if (serializedSession.HasValue)
        {
            try
            {
                var restored = await agent.DeserializeSessionAsync(
                    serializedSession.Value,
                    cancellationToken: ct).ConfigureAwait(false);
                return (restored, false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to deserialize text-channel session for thread {ThreadId}; starting a fresh session.",
                    threadId);
            }
        }

        var created = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
        return (created, true);
    }
}
