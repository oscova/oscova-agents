using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Definitions;

/// <summary>
/// Per-user personal context layered on top of a shared agent's system instruction.
/// </summary>
public interface ISharedAgentPersonalContextRepository
{
    /// <summary>
    /// Returns the body the consumer user has saved for <paramref name="agent"/>, or empty if none.
    /// </summary>
    Task<string> GetAsync(string userId, AgentDefinition agent, CancellationToken ct = default);

    /// <summary>Persists personal context; cancellation is honored before mutation admission, then the save completes.</summary>
    Task SaveAsync(string userId, AgentDefinition agent, string body, CancellationToken ct = default);

    /// <summary>Deletes saved personal context; cancellation is honored before each mutation, then that delete completes.</summary>
    Task<int> DeleteForUsersAsync(IEnumerable<string> userIds, AgentDefinition agent, CancellationToken ct = default);
}
