using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Runtime;

/// <summary>
/// Owns framework-level tool synchronization, agent caching, and agent construction.
/// </summary>
public interface IAgentOrchestrator : IAgentCacheInvalidator
{
    /// <summary>
    /// Gets the synchronized tool collection used by built agents.
    /// </summary>
    AgentToolCollection Tools { get; }

    /// <summary>
    /// Gets the cache of built agents, including exposed agent-as-tool dependencies.
    /// </summary>
    AIAgentCollection AIAgents { get; }

    /// <summary>Replaces registered tools after applying the caller's optional execution decorators.</summary>
    void SyncTools(
        IEnumerable<ToolsetRegistration> toolsets,
        IReadOnlyList<AIFunction> externalTools,
        IReadOnlyDictionary<string, ExternalToolMetadata> externalToolMetadata,
        System.Func<AIFunction, AIFunction>? decorate = null);

    /// <summary>
    /// Removes a single agent from the cache.
    /// </summary>
    void EvictAgent(string agentId);

    /// <summary>
    /// Clears both tools and cached agents.
    /// </summary>
    void InvalidateAll();

    /// <summary>
    /// Returns a cached agent or builds the requested agent and its exposed dependencies.
    /// </summary>
    Task<ChatClientAgent> GetOrCreateAgentAsync(
        AgentBuildRequest request,
        CancellationToken cancellationToken = default);
}
