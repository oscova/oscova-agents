using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Drives a non-streaming agent run that may surface <see cref="ToolApprovalRequestContent"/> items.
/// Routes each pending approval through <see cref="IToolApprovalRouter"/> and feeds the responses
/// back into the agent until the run finishes without further approval requests.
/// </summary>
public sealed class ToolApprovalRunLoop(IToolApprovalRouter router)
{
    public Task<AgentResponse> RunAsync(
        AIAgent agent,
        ChatMessage initialMessage,
        AgentSession session,
        AgentRunOptions? runOptions,
        ToolApprovalContext context,
        CancellationToken cancellationToken) =>
        RunAsync(agent, [initialMessage], session, runOptions, context, cancellationToken);

    public async Task<AgentResponse> RunAsync(
        AIAgent agent,
        IEnumerable<ChatMessage> initialMessages,
        AgentSession session,
        AgentRunOptions? runOptions,
        ToolApprovalContext context,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? validateBeforeRun = null,
        Action<AgentResponseUpdate>? onUpdate = null)
    {
        IEnumerable<ChatMessage> nextMessages = initialMessages;

        while (true)
        {
            if (validateBeforeRun is not null)
            {
                await validateBeforeRun(cancellationToken).ConfigureAwait(false);
            }

            AgentResponse response;
            if (onUpdate is null)
            {
                response = await agent.RunAsync(nextMessages, session, runOptions, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var updates = new List<AgentResponseUpdate>();
                await foreach (var update in agent.RunStreamingAsync(nextMessages, session, runOptions, cancellationToken).ConfigureAwait(false))
                {
                    updates.Add(update);
                    onUpdate(update);
                }

                response = updates.ToAgentResponse();
            }

            var pendingApprovals = ExtractApprovalRequests(response);
            if (pendingApprovals.Count == 0)
            {
                // A consulted parent settles only the workers it started, the same way a root run does.
                var settled = new RunContextAccessor().Current?.DelegatedWork is { } work
                    ? await work.SettleAsync(agent, session, null, cancellationToken).ConfigureAwait(false)
                    : null;
                if (settled is null)
                {
                    return response;
                }

                nextMessages = settled;
                continue;
            }

            var responseContents = new List<AIContent>(pendingApprovals.Count);
            foreach (var request in pendingApprovals)
            {
                var submission = await router.RouteAsync(request, context, cancellationToken).ConfigureAwait(false);
                responseContents.Add(submission.Response);
            }

            nextMessages = [new ChatMessage(ChatRole.User, responseContents)];
        }
    }

    private static List<ToolApprovalRequestContent> ExtractApprovalRequests(AgentResponse response)
    {
        var requests = new List<ToolApprovalRequestContent>();
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is ToolApprovalRequestContent request)
                {
                    requests.Add(request);
                }
            }
        }

        return requests;
    }
}
