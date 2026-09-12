using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Hooks;

/// <summary>
/// Dispatches configured external hooks for an agent lifecycle event.
/// </summary>
public interface IAgentHookDispatcher
{
    /// <summary>
    /// Dispatches the request to configured external hook handlers.
    /// </summary>
    Task<HookDispatchResult> DispatchAsync(HookDispatchRequest request, CancellationToken ct = default);
}