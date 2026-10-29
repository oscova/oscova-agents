using ModelContextProtocol.Protocol;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// MCP elicitation handler for text-only chat channels. Mirrors <see cref="IWebElicitationService"/>
/// boundary; lives in Core so Infrastructure can implement it.
/// </summary>
public interface ITextChannelElicitationService
{
    /// <summary>Runs the prompt → reply → parse → re-prompt loop. Cancel/timeout/retries-exhausted all map to <c>Action = "cancel"</c>.</summary>
    Task<ElicitResult> HandleMcpElicitationAsync(
        ElicitRequestParams requestParams,
        PluginElicitationOrigin origin,
        CancellationToken cancellationToken);
}
