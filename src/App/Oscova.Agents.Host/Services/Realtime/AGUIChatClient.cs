using Oscova.Agents.Conversations;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Host.Services.Realtime;

/// <summary>
/// SSE-based AG-UI client for CLI text streaming and interactive requests.
/// </summary>
public sealed class AGUIChatClient(HttpClient httpClient, ILogger<AGUIChatClient> logger)
{
    private const string ElicitationCallIdPrefix = "elicit_";
    private const string ToolApprovalCallIdPrefix = "approve_";
    private const string ToolApprovalToolName = "request_approval";

    public Func<ChatMessageDeltaEvent, Task>? OnMessageDeltaReceivedAsync { get; set; }
    public Func<ChatElicitationRequestedEvent, Task>? OnElicitationRequestedAsync { get; set; }
    public Func<ChatToolApprovalRequestedEvent, Task>? OnToolApprovalRequestedAsync { get; set; }

    /// <summary>
    /// Opens the AG-UI SSE stream for a single chat run. Returns when the run finishes,
    /// errors, or the caller cancels. Events are raised on the consumer's synchronization
    /// context as each SSE item is parsed.
    /// </summary>
    public async Task RunAsync(
        string threadId,
        string runId,
        string userText,
        string? agentId,
        string? modelProviderId,
        ToolApprovalRunMode approvalRunMode,
        IReadOnlyList<string> attachmentIds,
        CancellationToken ct)
    {
        var forwardedProps = new Dictionary<string, object?>
        {
            ["modelProviderId"] = modelProviderId,
            ["agentId"] = agentId,
            ["approvalRunMode"] = approvalRunMode.ToString(),
            ["attachmentIds"] = attachmentIds
        };

        var input = new
        {
            threadId,
            runId,
            messages = new[]
            {
                new { id = $"msg_{Guid.NewGuid():N}", role = "user", content = userText }
            },
            forwardedProps
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Agent.Run)
        {
            Content = JsonContent.Create(input)
        };

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var parser = SseParser.Create(stream);

        var state = new RunState(runId, threadId);

        await foreach (var sseItem in parser.EnumerateAsync(ct).ConfigureAwait(false))
        {
            // AG-UI emits all events as default-typed SSE items ("message"); the actual
            // event name lives in the JSON "type" discriminator, so parse from the payload.
            var eventType = ExtractEventType(sseItem.Data);
            if (eventType is null)
            {
                continue;
            }

            await HandleEventAsync(eventType, sseItem.Data, state).ConfigureAwait(false);
        }

        if (!state.RunFinished)
        {
            await EmitFinalDeltaAsync(state).ConfigureAwait(false);
        }
        }
    }

    private async Task HandleEventAsync(string eventType, string data, RunState state)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            switch (eventType)
            {
                case "TEXT_MESSAGE_START":
                    HandleTextStart(state, root);
                    break;
                case "TEXT_MESSAGE_CONTENT":
                    await HandleTextContentAsync(state, root).ConfigureAwait(false);
                    break;
                case "TEXT_MESSAGE_END":
                    state.SegmentParser.Complete();
                    await EmitDeltaAsync(state, isFinal: false).ConfigureAwait(false);
                    break;
                case "REASONING_START":
                    break;
                case "REASONING_MESSAGE_START":
                    HandleReasoningStart(state, root);
                    break;
                case "REASONING_MESSAGE_CONTENT":
                case "REASONING_MESSAGE_CHUNK": // Framework also emits this reasoning deltas shape
                    await HandleReasoningContentAsync(state, root).ConfigureAwait(false);
                    break;
                case "REASONING_MESSAGE_END":
                    state.SegmentParser.Complete();
                    await EmitDeltaAsync(state, isFinal: false).ConfigureAwait(false);
                    break;
                case "REASONING_END":
                case "REASONING_ENCRYPTED_VALUE": // Not useful for display, ignore.
                    break;
                case "TOOL_CALL_START":
                    await HandleToolCallStartAsync(state, root).ConfigureAwait(false);
                    break;
                case "TOOL_CALL_ARGS":
                    await HandleToolCallArgsAsync(state, root).ConfigureAwait(false);
                    break;
                case "TOOL_CALL_END":
                    await HandleToolCallEndAsync(state, root).ConfigureAwait(false);
                    break;
                case "TOOL_CALL_RESULT":
                    await HandleToolCallResultAsync(state, root).ConfigureAwait(false);
                    break;
                case "RUN_FINISHED":
                    state.RunFinished = true;
                    await EmitFinalDeltaAsync(state).ConfigureAwait(false);
                    break;
                case "RUN_ERROR":
                    var message = root.TryGetProperty("message", out var m) ? m.GetString() : "Run failed.";
                    throw new InvalidOperationException(message ?? "AG-UI run error.");
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse AG-UI event '{EventType}'.", eventType);
        }
    }

    private static void HandleTextStart(RunState state, JsonElement root)
    {
        var messageId = root.GetProperty("messageId").GetString() ?? Guid.NewGuid().ToString("N");
        state.EnsureBotMessage(messageId);
    }

    private async Task HandleTextContentAsync(RunState state, JsonElement root)
    {
        var delta = root.TryGetProperty("delta", out var d) ? d.GetString() : null;
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        var update = new AgentResponseUpdate(ChatRole.Assistant, [new TextContent(delta)]);
        state.SegmentParser.Process(update);
        await EmitDeltaAsync(state, isFinal: false).ConfigureAwait(false);
    }

    private static void HandleReasoningStart(RunState state, JsonElement root)
    {
        var messageId = root.TryGetProperty("messageId", out var m)
            ? m.GetString() ?? Guid.NewGuid().ToString("N")
            : Guid.NewGuid().ToString("N");
        state.EnsureBotMessage(messageId);
    }

    private async Task HandleReasoningContentAsync(RunState state, JsonElement root)
    {
        var delta = root.TryGetProperty("delta", out var d) ? d.GetString() : null;
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        if (root.TryGetProperty("messageId", out var messageIdProperty) &&
            messageIdProperty.GetString() is { } messageId &&
            !string.IsNullOrWhiteSpace(messageId))
        {
            state.EnsureBotMessage(messageId);
        }

        var update = new AgentResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(delta)]);
        state.SegmentParser.Process(update);
        await EmitDeltaAsync(state, isFinal: false).ConfigureAwait(false);
    }

    private async Task HandleToolCallStartAsync(RunState state, JsonElement root)
    {
        var toolCallId = root.GetProperty("toolCallId").GetString() ?? string.Empty;
        var toolCallName = root.GetProperty("toolCallName").GetString() ?? string.Empty;
        if (IsDisplayableToolCall(toolCallName))
        {
            state.EnsureBotMessage($"tool_msg_{toolCallId}");
            state.SegmentParser.StartToolCall(new FunctionCallContent(toolCallId, toolCallName, null));
            await EmitDeltaAsync(state, isFinal: false).ConfigureAwait(false);
        }

        state.OpenToolCalls[toolCallId] = new PendingToolCall
        {
            Id = toolCallId,
            Name = toolCallName,
            ArgumentsBuffer = new StringBuilder()
        };
    }

    private async Task HandleToolCallArgsAsync(RunState state, JsonElement root)
    {
        var toolCallId = root.GetProperty("toolCallId").GetString() ?? string.Empty;
        if (!state.OpenToolCalls.TryGetValue(toolCallId, out var pending))
        {
            return;
        }

        var delta = root.TryGetProperty("delta", out var d) ? d.GetString() : null;
        if (!string.IsNullOrEmpty(delta))
        {
            pending.ArgumentsBuffer.Append(delta);
        }

        if (IsDisplayableToolCall(pending.Name) &&
            ParseArguments(pending.ArgumentsBuffer.ToString()) is { } arguments)
        {
            state.SegmentParser.StartToolCall(new FunctionCallContent(pending.Id, pending.Name, arguments));
            await EmitDeltaAsync(state, isFinal: false).ConfigureAwait(false);
        }
    }

    private async Task HandleToolCallEndAsync(RunState state, JsonElement root)
    {
        var toolCallId = root.GetProperty("toolCallId").GetString() ?? string.Empty;
        if (!state.OpenToolCalls.TryGetValue(toolCallId, out var pending))
        {
            return;
        }

        state.OpenToolCalls.Remove(toolCallId);

        if (IsElicitationToolName(pending.Name))
        {
            await EmitElicitationRequestedAsync(state, pending).ConfigureAwait(false);
            return;
        }

        if (string.Equals(pending.Name, ToolApprovalToolName, StringComparison.Ordinal))
        {
            await EmitToolApprovalRequestedAsync(state, pending).ConfigureAwait(false);
            return;
        }

        var arguments = ParseArguments(pending.ArgumentsBuffer.ToString());
        var functionCall = new FunctionCallContent(pending.Id, pending.Name, arguments);
        var update = new AgentResponseUpdate(ChatRole.Assistant, [functionCall]);
        state.SegmentParser.Process(update);
        await EmitDeltaAsync(state, isFinal: false).ConfigureAwait(false);
    }

    private async Task HandleToolCallResultAsync(RunState state, JsonElement root)
    {
        var toolCallId = root.GetProperty("toolCallId").GetString() ?? string.Empty;
        if (toolCallId.StartsWith(ElicitationCallIdPrefix, StringComparison.Ordinal))
        {
            return;
        }

        if (toolCallId.StartsWith(ToolApprovalCallIdPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var result = ExtractToolResult(root);
        var update = new AgentResponseUpdate(ChatRole.Tool, [new FunctionResultContent(toolCallId, result)]);
        state.SegmentParser.Process(update);
        if (state.DetachedToolMessages.Remove(toolCallId, out var detachedMessage) &&
            OnMessageDeltaReceivedAsync is not null)
        {
            await OnMessageDeltaReceivedAsync.Invoke(
                new ChatMessageDeltaEvent(
                    state.RunId,
                    state.ThreadId,
                    detachedMessage,
                    IsFinal: false)).ConfigureAwait(false);
        }
        else
        {
            await EmitDeltaAsync(state, isFinal: false).ConfigureAwait(false);
        }
    }

    private static object? ExtractToolResult(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var result) &&
            !root.TryGetProperty("result", out result))
        {
            return null;
        }

        return result.ValueKind == JsonValueKind.String
            ? result.GetString()
            : result.GetRawText();
    }

    private async Task EmitElicitationRequestedAsync(RunState state, PendingToolCall pending)
    {
        var arguments = ParseArguments(pending.ArgumentsBuffer.ToString());
        if (arguments is null ||
            !arguments.TryGetValue("request", out var requestObj) ||
            requestObj is not JsonElement requestElement)
        {
            logger.LogWarning("Elicitation tool '{ToolName}' arrived without a 'request' payload.", pending.Name);
            return;
        }

        ElicitationRequest? elicitation;
        try
        {
            elicitation = requestElement.Deserialize<ElicitationRequest>(SerializerOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize elicitation request for tool '{ToolName}'.", pending.Name);
            return;
        }

        if (elicitation is null)
        {
            return;
        }

        // Close any open text segment so the elicitation card renders after preceding text,
        // mirroring the server-side "reset segments at elicitation boundary" behavior.
        state.SegmentParser.Complete();
        if (state.BotMessage is not null)
        {
            state.BotMessage.Segments = [.. state.SegmentParser.Segments];
            await EmitDeltaAsync(state, isFinal: false).ConfigureAwait(false);
            TrackDetachedToolMessages(state);
        }

        state.SegmentParser.Reset();

        var elicitationMessageId = $"elicit_msg_{elicitation.RequestId:N}";
        var elicitationMessage = new ChatDisplayMessage
        {
            Channel = HookExecutionKinds.WebChat,
            MessageId = elicitationMessageId,
            From = MessageOwner.Bot,
            Text = string.Empty,
            Elicitation = elicitation,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Begin a fresh bot message so subsequent text streams into a new bubble after the prompt.
        state.BotMessage = null;
        state.CurrentBotMessageId = null;

        if (OnElicitationRequestedAsync is not null)
        {
            await OnElicitationRequestedAsync.Invoke(
                new ChatElicitationRequestedEvent(state.RunId, state.ThreadId, elicitationMessage)).ConfigureAwait(false);
        }
    }

    private async Task EmitToolApprovalRequestedAsync(RunState state, PendingToolCall pending)
    {
        var arguments = ParseArguments(pending.ArgumentsBuffer.ToString());
        if (arguments is null ||
            !arguments.TryGetValue("request", out var requestObj) ||
            requestObj is not JsonElement requestElement)
        {
            logger.LogWarning("Tool approval call '{ToolName}' arrived without a 'request' payload.", pending.Name);
            return;
        }

        ToolApprovalRequest? approvalRequest;
        try
        {
            approvalRequest = requestElement.Deserialize<ToolApprovalRequest>(SerializerOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize tool approval request for tool '{ToolName}'.", pending.Name);
            return;
        }

        if (approvalRequest is null || string.IsNullOrEmpty(approvalRequest.RequestId))
        {
            return;
        }

        state.SegmentParser.Complete();
        if (state.BotMessage is not null)
        {
            state.BotMessage.Segments = [.. state.SegmentParser.Segments];
            await EmitDeltaAsync(state, isFinal: false).ConfigureAwait(false);
            TrackDetachedToolMessages(state);
        }

        state.SegmentParser.Reset();

        var approvalMessage = new ChatDisplayMessage
        {
            Channel = HookExecutionKinds.WebChat,
            MessageId = $"approve_msg_{approvalRequest.RequestId}",
            From = MessageOwner.Bot,
            Text = string.Empty,
            ToolApproval = approvalRequest,
            CreatedAt = DateTimeOffset.UtcNow
        };

        state.BotMessage = null;
        state.CurrentBotMessageId = null;

        if (OnToolApprovalRequestedAsync is not null)
        {
            await OnToolApprovalRequestedAsync.Invoke(
                new ChatToolApprovalRequestedEvent(state.RunId, state.ThreadId, approvalMessage)).ConfigureAwait(false);
        }
    }

    private async Task EmitDeltaAsync(RunState state, bool isFinal)
    {
        if (state.BotMessage is null || OnMessageDeltaReceivedAsync is null)
        {
            return;
        }

        state.BotMessage.Segments = [.. state.SegmentParser.Segments];
        state.BotMessage.Text = string.Concat(
            state.SegmentParser.Segments
                .Where(s => s.Type == ChatSegmentType.Text)
                .Select(s => s.Text));

        await OnMessageDeltaReceivedAsync.Invoke(
            new ChatMessageDeltaEvent(state.RunId, state.ThreadId, state.BotMessage, isFinal)).ConfigureAwait(false);
    }

    private async Task EmitFinalDeltaAsync(RunState state)
    {
        state.SegmentParser.Complete();
        await EmitDeltaAsync(state, isFinal: true).ConfigureAwait(false);
    }

    private static void TrackDetachedToolMessages(RunState state)
    {
        if (state.BotMessage is null)
        {
            return;
        }

        foreach (var segment in state.BotMessage.Segments)
        {
            if (segment.Type == ChatSegmentType.ToolCall &&
                segment.ExecutionState is ToolExecutionState.Pending or ToolExecutionState.Executing &&
                segment.FunctionCall?.CallId is { Length: > 0 } callId)
            {
                state.DetachedToolMessages[callId] = state.BotMessage;
            }
        }
    }

    private static bool IsElicitationToolName(string name) =>
        name is "request_boolean_input" or "request_string_input"
             or "request_number_input" or "request_enum_input";

    private static bool IsDisplayableToolCall(string name) =>
        !IsElicitationToolName(name) &&
        !string.Equals(name, ToolApprovalToolName, StringComparison.Ordinal);

    private static string? ExtractEventType(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                ? typeProp.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IDictionary<string, object?>? ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            // Named tool arguments can only be read from a JSON object.
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { return null; }

            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.Clone();
            }

            return dict;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private sealed class RunState(string runId, string threadId)
    {
        public string RunId { get; } = runId;
        public string ThreadId { get; } = threadId;
        public ChatSegmentParser SegmentParser { get; } = new();
        public ChatDisplayMessage? BotMessage { get; set; }
        public string? CurrentBotMessageId { get; set; }
        public IDictionary<string, PendingToolCall> OpenToolCalls { get; } = new Dictionary<string, PendingToolCall>(StringComparer.Ordinal);
        public Dictionary<string, ChatDisplayMessage> DetachedToolMessages { get; } = new(StringComparer.Ordinal);
        public bool RunFinished { get; set; }

        public void EnsureBotMessage(string messageId)
        {
            if (BotMessage is not null)
            {
                CurrentBotMessageId = messageId;
                return;
            }

            BotMessage = new ChatDisplayMessage
            {
                Channel = HookExecutionKinds.WebChat,
                MessageId = messageId,
                From = MessageOwner.Bot,
                Text = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow
            };
            CurrentBotMessageId = messageId;
        }
    }

    private sealed class PendingToolCall
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required StringBuilder ArgumentsBuffer { get; init; }
    }
}
