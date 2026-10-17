using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Middleware;

/// <summary>
/// Observes each model API call made by an agent during a run.
/// Implemented by consumers that need live visibility into the agent pipeline.
/// </summary>
public interface IAgentRunObserver
{
    /// <summary>
    /// Called immediately before a request is sent to the model client.
    /// </summary>
    /// <param name="messages">The messages that will be submitted to the model.</param>
    /// <param name="options">The request options that contain the assembled instruction and offered tools.</param>
    /// <param name="cancellationToken">Cancels preparation before the model receives the request.</param>
    Task OnRequestAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called after the model client returns a response.
    /// </summary>
    /// <param name="response">The response returned by the model client.</param>
    void OnResponse(ChatResponse response);
}
