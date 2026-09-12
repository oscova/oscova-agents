using Microsoft.Extensions.AI;

namespace Oscova.Agents.Middleware;

/// <summary>
/// Observer that receives tool execution lifecycle notifications from the tracking middleware.
/// Callbacks receive the full <see cref="FunctionCallContent"/> so observers can inspect
/// the call id and arguments in addition to the function name.
/// </summary>
public interface IToolTrackingObserver
{
    /// <summary>
    /// Called when a tool call has been parsed and is about to execute.
    /// </summary>
    /// <param name="functionCall">The function call metadata for the tool execution.</param>
    void OnToolStarted(FunctionCallContent functionCall);

    /// <summary>
    /// Called when a tool call completes successfully.
    /// </summary>
    /// <param name="functionCall">The function call metadata for the completed tool execution.</param>
    void OnToolCompleted(FunctionCallContent functionCall);

    /// <summary>
    /// Called when a tool call fails.
    /// </summary>
    /// <param name="functionCall">The function call metadata for the failed tool execution.</param>
    void OnToolFailed(FunctionCallContent functionCall);
}
