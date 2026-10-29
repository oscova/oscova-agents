using Oscova.Agents.Conversations;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Hooks;
using System.Text.Json;

namespace Oscova.Agents.Infrastructure.Conversations;

public class WebElicitationService(ILogger<WebElicitationService> logger) : IWebElicitationService
{
    private readonly Dictionary<Guid, PendingElicitation> _pendingElicitations = [];
    private readonly Lock _elicitationLock = new();

    public async Task<ElicitResult> HandleMcpElicitationAsync(
        ElicitRequestParams requestParams,
        ChatThread chatThread,
        Action onStateChanged,
        CancellationToken token)
    {
        var request = ElicitationUtilities.BuildRequest(requestParams, logger);
        if (request is null)
        {
            return new ElicitResult { Action = "cancel" };
        }

        var displayMessage = new ChatDisplayMessage
        {
            Channel = HookExecutionKinds.WebChat,
            From = MessageOwner.Bot,
            Text = string.Empty,
            Segments = [],
            Elicitation = request
        };

        var completion = new TaskCompletionSource<ElicitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingElicitation
        {
            Id = request.RequestId,
            DisplayMessage = displayMessage,
            Request = request,
            Completion = completion,
            ChatThread = chatThread,
            OnStateChanged = onStateChanged
        };

        lock (_elicitationLock)
        {
            _pendingElicitations[request.RequestId] = pending;
        }

        AddDisplayMessage(chatThread, displayMessage);
        onStateChanged?.Invoke();

        logger.LogDebug("Displayed elicitation request {RequestId} to the user.", request.RequestId);

        using var registration = token.Register(() => CancelPendingElicitation(request.RequestId));
        return await completion.Task.ConfigureAwait(false);
    }

    public Task SubmitBooleanElicitationAsync(Guid requestId, bool? value, bool isCancel)
    {
        if (!TryStartElicitationSubmission(requestId,
            ElicitationSchemaType.Boolean, isCancel,
            request => value is bool answer ? ElicitationUtilities.BuildBooleanContent(request.BooleanFields, answer) : null,
            out var pending, out var displayChanged, out var chatThread, out var content))
        {
            logger.LogWarning("No pending elicitation found for request {RequestId}.", requestId);
            return Task.CompletedTask;
        }

        ChatDisplayMessage? acknowledgement = null;

        if (isCancel)
        {
            logger.LogDebug("Elicitation {RequestId} cancelled by the user.", requestId);
            _ = pending.Completion.TrySetResult(new ElicitResult { Action = "cancel" });
        }
        else if (value is bool boolValue)
        {
            _ = pending.Completion.TrySetResult(new ElicitResult { Action = "accept", Content = content });

            acknowledgement = CreateInteractionAcknowledgement(boolValue ? "Yes" : "No");

            logger.LogDebug("Elicitation {RequestId} answered with {Value}.", requestId, boolValue);
        }
        else
        {
            logger.LogDebug("Elicitation {RequestId} rejected by the user.", requestId);
            _ = pending.Completion.TrySetResult(new ElicitResult { Action = "reject" });
        }

        if (acknowledgement is not null)
        {
            AddDisplayMessage(chatThread, acknowledgement);
            chatThread.ElicitationGenerated?.Invoke();
            displayChanged = true;
        }

        if (displayChanged)
        {
            pending.OnStateChanged?.Invoke();
        }

        return Task.CompletedTask;
    }

    public Task SubmitStringElicitationAsync(Guid requestId, IReadOnlyDictionary<string, string> values, bool isCancel)
    {
        if (!TryStartElicitationSubmission(requestId,
            ElicitationSchemaType.String, isCancel,
            request => values is null ? null : ElicitationUtilities.BuildStringContent(request.StringFields, values),
            out var pending, out var displayChanged, out var chatThread, out var content))
        {
            logger.LogWarning("No pending elicitation found for request {RequestId}.", requestId);
            return Task.CompletedTask;
        }

        ChatDisplayMessage? acknowledgement = null;

        if (isCancel)
        {
            logger.LogDebug("Elicitation {RequestId} cancelled by the user.", requestId);
            _ = pending.Completion.TrySetResult(new ElicitResult { Action = "cancel" });
        }
        else if (values is not null)
        {
            var acknowledgementPairs = content!.Keys
                .Select(k => new KeyValuePair<string, string>(k, values.TryGetValue(k, out var v) ? v ?? string.Empty : string.Empty))
                .ToList();

            _ = pending.Completion.TrySetResult(new ElicitResult { Action = "accept", Content = content });

            acknowledgement = CreateInteractionAcknowledgement(
                BuildAcknowledgementText(pending.Request, acknowledgementPairs));

            logger.LogDebug("Elicitation {RequestId} answered with string values: {Values}.",
                requestId,
                string.Join(", ", acknowledgementPairs.Select(p => $"{p.Key}={p.Value}")));
        }
        else
        {
            logger.LogDebug("Elicitation {RequestId} rejected by the user.", requestId);
            _ = pending.Completion.TrySetResult(new ElicitResult { Action = "reject" });
        }

        if (acknowledgement is not null)
        {
            AddDisplayMessage(chatThread, acknowledgement);
            chatThread.ElicitationGenerated?.Invoke();
            displayChanged = true;
        }

        if (displayChanged)
        {
            pending.OnStateChanged?.Invoke();
        }

        return Task.CompletedTask;
    }

    public Task SubmitNumberElicitationAsync(Guid requestId, IReadOnlyDictionary<string, (JsonElement Value, bool IsInteger)> values, bool isCancel)
    {
        if (!TryStartElicitationSubmission(requestId,
            ElicitationSchemaType.Number, isCancel,
            request => values?.Where(pair => request.NumberFields.Any(field =>
                string.Equals(field.Name, pair.Key, StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.OrdinalIgnoreCase),
            out var pending, out var displayChanged, out var chatThread, out var content))
        {
            logger.LogWarning("No pending elicitation found for request {RequestId}.", requestId);
            return Task.CompletedTask;
        }

        ChatDisplayMessage? acknowledgement = null;

        if (isCancel)
        {
            logger.LogDebug("Elicitation {RequestId} cancelled by the user.", requestId);
            _ = pending.Completion.TrySetResult(new ElicitResult { Action = "cancel" });
        }
        else if (values is not null)
        {
            var acknowledgementPairs = content!.Select(pair =>
                new KeyValuePair<string, string>(pair.Key, pair.Value.ToString())).ToList();

            _ = pending.Completion.TrySetResult(new ElicitResult
            {
                Action = "accept",
                Content = content
            });

            acknowledgement = CreateInteractionAcknowledgement(
                BuildAcknowledgementText(pending.Request, acknowledgementPairs));

            logger.LogDebug("Elicitation {RequestId} answered with numeric values: {Values}.",
                requestId,
                string.Join(", ", acknowledgementPairs.Select(p => $"{p.Key}={p.Value}")));
        }
        else
        {
            logger.LogDebug("Elicitation {RequestId} rejected by the user.", requestId);
            _ = pending.Completion.TrySetResult(new ElicitResult { Action = "reject" });
        }

        if (acknowledgement is not null)
        {
            AddDisplayMessage(chatThread, acknowledgement);
            chatThread.ElicitationGenerated?.Invoke();
            displayChanged = true;
        }

        if (displayChanged)
        {
            pending.OnStateChanged?.Invoke();
        }

        return Task.CompletedTask;
    }

    public Task SubmitEnumElicitationAsync(Guid requestId, IReadOnlyDictionary<string, string> values, bool isCancel)
    {
        if (!TryStartElicitationSubmission(requestId,
            ElicitationSchemaType.Enum, isCancel,
            request => values is null ? null : ElicitationUtilities.BuildEnumContent(request.EnumFields, values),
            out var pending, out var displayChanged, out var chatThread, out var content))
        {
            logger.LogWarning("No pending elicitation found for request {RequestId}.", requestId);
            return Task.CompletedTask;
        }

        ChatDisplayMessage? acknowledgement = null;

        if (isCancel)
        {
            logger.LogDebug("Elicitation {RequestId} cancelled by the user.", requestId);
            _ = pending.Completion.TrySetResult(new ElicitResult { Action = "cancel" });
        }
        else if (values is not null)
        {
            var acknowledgementPairs = new List<KeyValuePair<string, string>>();

            foreach (var key in content!.Keys)
            {
                var value = values.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;
                var displayLabel = value;
                var fieldDef = pending.Request.EnumFields.FirstOrDefault(f => string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));
                if (fieldDef is not null)
                {
                    var matchedOption = fieldDef.Options.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.Ordinal));
                    if (matchedOption is not null && !string.IsNullOrWhiteSpace(matchedOption.Label))
                        displayLabel = matchedOption.Label;
                }

                acknowledgementPairs.Add(new KeyValuePair<string, string>(key, displayLabel));
            }

            _ = pending.Completion.TrySetResult(new ElicitResult { Action = "accept", Content = content });

            acknowledgement = CreateInteractionAcknowledgement(
                BuildAcknowledgementText(pending.Request, acknowledgementPairs));

            logger.LogDebug("Elicitation {RequestId} answered with enum values: {Values}.",
                requestId,
                string.Join(", ", acknowledgementPairs.Select(p => $"{p.Key}={p.Value}")));
        }
        else
        {
            logger.LogDebug("Elicitation {RequestId} rejected by the user.", requestId);
            _ = pending.Completion.TrySetResult(new ElicitResult { Action = "reject" });
        }

        if (acknowledgement is not null)
        {
            AddDisplayMessage(chatThread, acknowledgement);
            chatThread.ElicitationGenerated?.Invoke();
            displayChanged = true;
        }

        if (displayChanged)
        {
            pending.OnStateChanged?.Invoke();
        }

        return Task.CompletedTask;
    }

    public void CancelPendingElicitation(Guid requestId)
    {
        PendingElicitation? pending;

        lock (_elicitationLock)
        {
            if (!_pendingElicitations.TryGetValue(requestId, out pending))
            {
                return;
            }

            _pendingElicitations.Remove(requestId);
        }

        var removed = RemoveDisplayMessage(pending.ChatThread, pending.DisplayMessage);
        _ = pending.Completion.TrySetResult(new ElicitResult { Action = "cancel" });

        if (removed)
        {
            pending.OnStateChanged?.Invoke();
        }

        logger.LogDebug("Elicitation {RequestId} cancelled due to session update or timeout.", requestId);
    }

    public void CancelAllPendingElicitations()
    {
        PendingElicitation[] pending;

        lock (_elicitationLock)
        {
            if (_pendingElicitations.Count == 0)
            {
                return;
            }

            pending = [.. _pendingElicitations.Values];
            _pendingElicitations.Clear();
        }

        var displayChanged = false;
        Action? notifyAction = null;

        foreach (var item in pending)
        {
            var removed = RemoveDisplayMessage(item.ChatThread, item.DisplayMessage);
            displayChanged |= removed;
            _ = item.Completion.TrySetResult(new ElicitResult { Action = "cancel" });
            logger.LogDebug("Elicitation {RequestId} cancelled while tearing down the MCP session.", item.Id);

            notifyAction ??= item.OnStateChanged;
        }

        if (displayChanged && notifyAction is not null)
        {
            notifyAction.Invoke();
        }
    }

    private bool TryStartElicitationSubmission(
        Guid requestId, ElicitationSchemaType schemaType, bool isCancel,
        Func<ElicitationRequest, IDictionary<string, JsonElement>?> buildContent,
        out PendingElicitation pending, out bool displayChanged, out ChatThread chatThread,
        out IDictionary<string, JsonElement>? content)
    {
        PendingElicitation? localPending;
        content = null;

        lock (_elicitationLock)
        {
            if (!_pendingElicitations.TryGetValue(requestId, out localPending))
            {
                pending = null!;
                displayChanged = false;
                chatThread = null!;
                return false;
            }

            // Build and validate while the request is still cancellable and visible.
            if (!isCancel)
            {
                if (localPending.Request.SchemaType != schemaType)
                {
                    throw new ArgumentException("Reply type does not match the pending request.");
                }

                content = buildContent(localPending.Request);
                if (content is not null)
                {
                    content = new Dictionary<string, JsonElement>(content, StringComparer.OrdinalIgnoreCase);
                    ElicitationUtilities.ValidateResponse(localPending.Request, content);
                }
            }

            _pendingElicitations.Remove(requestId);
        }

        pending = localPending!;
        chatThread = pending.ChatThread;
        displayChanged = RemoveDisplayMessage(chatThread, pending.DisplayMessage);
        return true;
    }

    private static void AddDisplayMessage(ChatThread chatThread, ChatDisplayMessage message)
    {
        lock (chatThread.DisplayMessages)
        {
            chatThread.DisplayMessages.Add(message);
        }
    }

    private static ChatDisplayMessage CreateInteractionAcknowledgement(string text) => new()
    {
        Channel = HookExecutionKinds.WebChat,
        From = MessageOwner.User,
        Text = text,
        IsInteractionResponse = true
    };

    private static bool RemoveDisplayMessage(ChatThread chatThread, ChatDisplayMessage message)
    {
        lock (chatThread.DisplayMessages)
        {
            return chatThread.DisplayMessages.Remove(message);
        }
    }

    private static string BuildAcknowledgementText(ElicitationRequest request, IEnumerable<KeyValuePair<string, string>> values)
    {
        var materialized = values.ToList();
        if (materialized.Count == 0)
        {
            return "Submitted response";
        }

        return string.Join(Environment.NewLine, materialized.Select(pair =>
        {
            var label = GetFieldLabel(request, pair.Key);
            return string.IsNullOrWhiteSpace(pair.Value) ? label : $"{label}: {pair.Value}";
        }));
    }

    private static string GetFieldLabel(ElicitationRequest request, string fieldName)
    {
        return request.SchemaType switch
        {
            ElicitationSchemaType.Boolean => request.BooleanFields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))?.Label ?? fieldName,
            ElicitationSchemaType.String => request.StringFields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))?.Label ?? fieldName,
            ElicitationSchemaType.Number => request.NumberFields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))?.Label ?? fieldName,
            ElicitationSchemaType.Enum => request.EnumFields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))?.Label ?? fieldName,
            _ => fieldName
        };
    }

    private sealed class PendingElicitation
    {
        public required Guid Id { get; init; }
        public required ChatDisplayMessage DisplayMessage { get; init; }
        public required ElicitationRequest Request { get; init; }
        public required TaskCompletionSource<ElicitResult> Completion { get; init; }
        public required ChatThread ChatThread { get; init; }
        public required Action OnStateChanged { get; init; }
    }
}
