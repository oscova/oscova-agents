using System.Runtime.CompilerServices;
using System.Security.Claims;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.Mcp;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Host.Server.Chat;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Endpoints;

/// <summary>Maps AG-UI chat while separating viewer lifetime from run lifetime.</summary>
public static class ChatAgentStreamEndpoint
{
    /// <summary>Maps the server-owned AG-UI chat endpoint.</summary>
    public static IEndpointConventionBuilder MapChatAgentStream(
        this IEndpointRouteBuilder endpoints,
        string pattern)
    {
        return endpoints.MapPost(pattern, (
            RunAgentInput? input,
            IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
            ChatRunHost runHost,
            Oscova.Agents.Secrets.IContentProtector contentProtector,
            UserServiceManager userServiceManager,
            IAgentCatalog agentCatalog,
            HttpContext httpContext,
            CancellationToken requestAborted) =>
        {
            if (input is null)
            {
                return Results.BadRequest();
            }

            var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Unauthorized();
            }

            if (!contentProtector.IsAvailable(userId))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Vault locked",
                    detail: "Unlock your Vault Password before continuing.");
            }

            input.ThreadId = string.IsNullOrWhiteSpace(input.ThreadId)
                ? Guid.NewGuid().ToString("N")
                : input.ThreadId;
            input.RunId = string.IsNullOrWhiteSpace(input.RunId)
                ? Guid.NewGuid().ToString("N")
                : input.RunId;

            var context = input.ToChatRequestContext(
                jsonOptions.Value.SerializerOptions,
                streamOptions: null);
            var stream = runHost.TryStartAttached(userId, context, requestAborted);
            if (stream is null)
            {
                return Results.Conflict("That chat thread is already generating a response.");
            }

            return TypedResults.ServerSentEvents(
                ReadEventsAsync(stream, context, text => RelabelIds(userServiceManager, agentCatalog, userId, text), requestAborted));
        });
    }

    private static async IAsyncEnumerable<BaseEvent> ReadEventsAsync(
        ChatRunStream stream,
        ChatRequestContext context,
        Func<string, string> relabelIds,
        [EnumeratorCancellation] CancellationToken requestAborted)
    {
        Exception? failure = null;
        var events = stream.Reader
            .ReadAllAsync(requestAborted)
            .AsChatResponseUpdatesAsync()
            .AsAGUIEventStreamAsync(context);

        await using var enumerator = events.GetAsyncEnumerator(requestAborted);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
                {
                    yield break;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }

                if (!hasNext)
                {
                    break;
                }

                yield return enumerator.Current is RunErrorEvent error
                    ? stream.Failure is NodeUnavailableException or McpConnectionException or ProjectFolderUnavailableException
                        ? new RunErrorEvent { Code = error.Code, Message = relabelIds(RecoveryMessage(stream.Failure)) }
                        : new RunErrorEvent { Code = error.Code, Message = relabelIds(error.Message) }
                    : enumerator.Current;
            }

            if (failure is not null)
            {
                yield return new RunErrorEvent
                {
                    Code = "RunFailed",
                    // Only known recovery exceptions supply text, raw transport errors may contain secrets.
                    Message = failure is NodeUnavailableException or McpConnectionException or ProjectFolderUnavailableException ? relabelIds(RecoveryMessage(failure)) : "The chat response could not be completed."
                };
            }
        }
        finally
        {
            stream.Detach();
        }
    }

    // Run errors can name provider and MCP server ids; the user sees its own labels.
    private static string RelabelIds(UserServiceManager userServiceManager, IAgentCatalog agentCatalog, string userId, string text) =>
        userServiceManager.TryGetExistingScope(userId, out var scope) ? UserLabelMap.Create(scope, agentCatalog).ReplaceIds(text) : text;

    // The folder exception's own text describes project creation, so a run gets the recovery step the chat panel shows.
    private static string RecoveryMessage(Exception failure) => failure is ProjectFolderUnavailableException
        ? "The project folder is missing. Restore it, then try again."
        : failure.Message;
}
