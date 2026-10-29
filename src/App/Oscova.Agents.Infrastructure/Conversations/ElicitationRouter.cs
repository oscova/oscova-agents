using Oscova.Agents.Conversations;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Localization;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Infrastructure.Conversations;

public class ElicitationRouter(
    IWebElicitationService webElicitationService,
    ITextChannelElicitationService textChannelElicitationService,
    UserServiceManager userServiceManager,
    ILogger<ElicitationRouter> logger) : IElicitationRouter
{
    public async Task<ElicitResult> RouteElicitationAsync(
        ElicitRequestParams requestParams,
        ElicitationContext context,
        CancellationToken cancellationToken)
    {
        return context.Channel switch
        {
            ElicitationChannel.WebUI => await HandleWebElicitationAsync(
                requestParams, context, cancellationToken).ConfigureAwait(false),

            // Plugin channels share the renderer and carry their original reply callback.
            ElicitationChannel.Plugin => await HandleTextChannelElicitationAsync(
                requestParams, context, cancellationToken).ConfigureAwait(false),

            // Background tasks cannot prompt for user input — auto-deny.
            ElicitationChannel.BackgroundTask => new ElicitResult { Action = "cancel" },

            _ => throw new NotSupportedException(
                $"Elicitation channel {context.Channel} is not supported.")
        };
    }

    private async Task<ElicitResult> HandleWebElicitationAsync(
        ElicitRequestParams requestParams,
        ElicitationContext context,
        CancellationToken cancellationToken)
    {
        if (context.ChannelMetadata is not WebElicitationMetadata metadata)
        {
            logger.LogError("Missing WebElicitationMetadata for web UI elicitation.");
            return new ElicitResult { Action = "cancel" };
        }

        return await webElicitationService.HandleMcpElicitationAsync(
            requestParams,
            metadata.ChatThread,
            metadata.OnStateChanged,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ElicitResult> HandleTextChannelElicitationAsync(
        ElicitRequestParams requestParams,
        ElicitationContext context,
        CancellationToken cancellationToken)
    {
        if (context.ChannelMetadata is not TextChannelElicitationMetadata metadata)
        {
            logger.LogError("Missing TextChannelElicitationMetadata for {Channel} elicitation.", context.Channel);
            return new ElicitResult { Action = "cancel" };
        }

        // Establish the user's culture once so prompt and re-prompt resources resolve consistently.
        var scope = await userServiceManager.GetOrCreateScopeAsync(context.UserId, cancellationToken).ConfigureAwait(false);
        using var cultureScope = new CultureScope(scope.UiSettings.CultureName);

        return await textChannelElicitationService.HandleMcpElicitationAsync(
            requestParams,
            metadata.ChannelOriginator,
            cancellationToken).ConfigureAwait(false);
    }
}

public class WebElicitationMetadata
{
    public required ChatThread ChatThread { get; init; }
    public required Action OnStateChanged { get; init; }
}
