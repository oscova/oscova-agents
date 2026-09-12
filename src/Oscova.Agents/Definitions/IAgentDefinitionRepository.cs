using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Definitions;

/// <summary>
/// Persists and retrieves agent definitions for a host-provided user scope.
/// </summary>
public interface IAgentDefinitionRepository
{
    /// <summary>
    /// Retrieves one agent definition visible to the user.
    /// </summary>
    Task<AgentDefinition?> GetAgentAsync(string userId, string agentId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the user's main agent definition.
    /// </summary>
    Task<AgentDefinition?> GetMainAgentAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all agent definitions visible to the user.
    /// </summary>
    Task<IReadOnlyList<AgentDefinition>> GetAllAgentsAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Persists an agent definition for the user.
    /// </summary>
    Task SaveAgentAsync(string userId, AgentDefinition agent, CancellationToken ct = default);

    /// <summary>
    /// Ensures the user has a main agent definition.
    /// </summary>
    Task EnsureMainAgentAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Deletes an agent definition for the user.
    /// </summary>
    Task DeleteAgentAsync(string userId, string agentId, CancellationToken ct = default);
}
