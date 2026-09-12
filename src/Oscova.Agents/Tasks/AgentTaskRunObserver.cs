using System.Collections.Generic;
using System.Linq;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Middleware;

namespace Oscova.Agents.Tasks;

/// <summary>Projects the first current-run user message, eligible tool-result messages, non-streaming responses, and the first assembled instruction into one task run.</summary>
/// <param name="run">The task run that receives observed instruction and message state.</param>
public sealed class AgentTaskRunObserver(AgentTaskRun run) : IAgentRunObserver
{
    private readonly HashSet<string> _addedResultCallIds = [];
    private bool _capturedInitialUserMessage;

    /// <inheritdoc />
    public void OnRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        CaptureSystemInstruction(run, options);
        var snapshot = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        var added = false;
        foreach (var message in snapshot)
        {
            if (message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.ChatHistory)
            {
                continue;
            }

            var results = message.Contents.OfType<FunctionResultContent>().ToList();
            if (!_capturedInitialUserMessage &&
                message.Role == ChatRole.User &&
                results.Count == 0)
            {
                run.AddLiveMessage(message);
                _capturedInitialUserMessage = true;
                added = true;
            }

            if (results.Count == 0) continue;

            var hasNewResult = false;
            foreach (var result in results)
            {
                if (result.CallId is null)
                {
                    // Always treat results without a CallId as new, but do not track them in the deduplication set.
                    hasNewResult = true;
                }
                else if (_addedResultCallIds.Add(result.CallId))
                {
                    hasNewResult = true;
                }
            }

            if (hasNewResult)
            {
                run.AddLiveMessage(message);
                added = true;
            }
        }
        if (added) run.LiveMessagesChanged?.Invoke();
    }

    /// <inheritdoc />
    public void OnResponse(ChatResponse response)
    {
        run.AddLiveMessages(response.Messages);
        run.LiveMessagesChanged?.Invoke();
    }

    private static void CaptureSystemInstruction(AgentTaskRun run, ChatOptions? options)
    {
        if (string.IsNullOrWhiteSpace(run.SystemInstruction) &&
            !string.IsNullOrWhiteSpace(options?.Instructions))
        {
            run.SystemInstruction = options.Instructions;
        }
    }
}
