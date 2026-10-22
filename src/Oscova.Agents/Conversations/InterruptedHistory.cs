using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Oscova.Agents.Conversations;

/// <summary>Rebuilds a session history after a stopped or failed run.</summary>
public static class InterruptedHistory
{
    // ApprovalResponseBindingChatClient.StateBagKey, which Agent Framework keeps internal.
    private const string PendingApprovalsStateKey = "_pendingApprovalRequests";

    /// <summary>Replaces the session history after a stopped or failed run.</summary>
    [SuppressMessage("Design", "MA0016", Justification = "SetInMemoryChatHistory takes a List<ChatMessage>.")]
    public static void Restore(AgentSession session, List<ChatMessage> history)
    {
        session.SetInMemoryChatHistory(history);
        // The rebuilt history drops approval requests and approved calls that never finished. Agent Framework
        // still records them as pending and would replay them as rejected on the next run.
        session.StateBag.TryRemoveValue(PendingApprovalsStateKey);
    }

    /// <summary>Retains emitted native content while removing unfinished tool exchanges.</summary>
    [SuppressMessage("Design", "MA0016", Justification = "SetInMemoryChatHistory takes a List<ChatMessage>.")]
    public static List<ChatMessage> Build(
        IReadOnlyList<ChatMessage> historyBeforeRun,
        IReadOnlyList<ChatMessage> inputMessages,
        IEnumerable<ChatResponseUpdate> updates)
    {
        var messages = updates.ToChatResponse().Messages;
        var calls = messages.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>().Select(call => call.CallId).ToHashSet(StringComparer.Ordinal);
        var results = messages.SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>().Select(result => result.CallId).ToHashSet(StringComparer.Ordinal);
        calls.IntersectWith(results);

        // A canceled call may have changed external state, but has no confirmed native result.
        foreach (var message in messages)
        {
            message.Contents = message.Contents.Where(content => content switch
            {
                FunctionCallContent call => calls.Contains(call.CallId),
                FunctionResultContent result => calls.Contains(result.CallId),
                _ => true
            }).ToList();
        }

        return [.. historyBeforeRun, .. inputMessages,
            .. messages.Where(message => message.Contents.Count > 0)];
    }
}
