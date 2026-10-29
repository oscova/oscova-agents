using ModelContextProtocol.Protocol;

namespace Oscova.Agents.Core.Conversation;

public interface IElicitationRouter
{
    Task<ElicitResult> RouteElicitationAsync(
        ElicitRequestParams requestParams,
        ElicitationContext context,
        CancellationToken cancellationToken);
}

public class ElicitationContext
{
    public required string UserId { get; init; }
    public required ElicitationChannel Channel { get; init; }
    public object? ChannelMetadata { get; init; } // Discord message, web session, etc.
}
