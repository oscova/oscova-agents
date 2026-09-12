using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Middleware;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Oscova.Agents.Conversations;

/// <summary>Parses streaming responses into display-ready chat segments.</summary>
public class ChatSegmentParser : IToolTrackingObserver
{
    private readonly List<ChatSegment> _segments = [];
    private readonly List<ChatSegment> _detachedToolSegments = [];
    private ChatSegment? _openSegment;

    /// <summary>Runs when a segment's state, content, or progress changes.</summary>
    public Action? OnSegmentChanged;

    /// <summary>Live list of parsed segments. Grows as updates are processed.</summary>
    public ReadOnlyCollection<ChatSegment> Segments => _segments.AsReadOnly();

    /// <summary>Feed a streaming update into the parser to produce segments.</summary>
    public void Process(AgentResponseUpdate update)
    {
        if (update.Contents != null)
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent fc:
                        HandleFunctionCallContent(update, fc);
                        break;
                    case FunctionResultContent fr:
                        HandleFunctionResultContent(update, fr);
                        break;
                    case TextReasoningContent trc:
                        HandleReasoningContent(update, trc.Text);
                        break;
                    case TextContent tc:
                        HandleTextContent(update, tc.Text);
                        break;
                }
            }
        }
        else if (!string.IsNullOrEmpty(update.Text))
        {
            HandleTextContent(update, update.Text);
        }

        OnSegmentChanged?.Invoke();
    }

    /// <summary>Finalize any open segments at the end of a stream pass.</summary>
    public void Complete()
    {
        // RUN_FINISHED/end-of-stream closes any active AG-UI message/tool segment before consumers see final state.
        if (_openSegment != null && _openSegment.State != ChatSegmentState.Completed)
        {
            _openSegment.State = ChatSegmentState.Completed;
        }

        _openSegment = null;
        OnSegmentChanged?.Invoke();
    }

    /// <summary>Clear all segments and reset internal state (for elicitation partitioning).</summary>
    public void Reset()
    {
        // Preserve unresolved tool segments so later results can update the segment snapshot
        // already handed to the UI or persisted transcript.
        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            var seg = _segments[i];
            if (seg.Type == ChatSegmentType.ToolCall &&
                seg.ExecutionState is ToolExecutionState.Pending or ToolExecutionState.Executing)
            {
                _detachedToolSegments.Add(seg);
            }
        }

        _segments.Clear();
        _openSegment = null;
        OnSegmentChanged?.Invoke();
    }

    /// <summary>Returns the most recently created segment of a given type, or null.</summary>
    public ChatSegment? LastSegmentOfType(ChatSegmentType type)
    {
        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            if (_segments[i].Type == type)
            {
                return _segments[i];
            }
        }

        return null;
    }

    /// <summary>Open or refresh an incomplete tool-call segment before its final arguments are known.</summary>
    public void StartToolCall(FunctionCallContent functionCall)
    {
        // AG-UI permits surfacing a tool as soon as TOOL_CALL_START arrives; keep the pending card before final args.
        CloseIfDifferent(ChatSegmentType.ToolCall);

        var seg = FindSegmentForFunctionCall(functionCall.CallId);
        if (seg is null || seg.State == ChatSegmentState.Completed)
        {
            seg = CreateSegment(ChatSegmentType.ToolCall);
        }
        else
        {
            PromoteInProgress(seg);
        }

        seg.FunctionCall = functionCall;
        seg.ExecutionState = ToolExecutionState.Pending;
        _openSegment = seg;
        OnSegmentChanged?.Invoke();
    }

    /// <summary>Marks the matching tool-call segment as executing.</summary>
    public void OnToolStarted(FunctionCallContent functionCall)
    {
        var seg = FindSegmentForFunction(functionCall.Name);
        if (seg != null)
        {
            seg.ExecutionState = ToolExecutionState.Executing;
            OnSegmentChanged?.Invoke();
        }
    }

    /// <summary>Marks the matching tool-call segment as completed.</summary>
    public void OnToolCompleted(FunctionCallContent functionCall)
    {
        var seg = FindSegmentForFunction(functionCall.Name, ToolExecutionState.Executing);
        if (seg != null)
        {
            seg.ExecutionState = ToolExecutionState.Completed;
            seg.ProcessedAt = DateTimeOffset.UtcNow;
            OnSegmentChanged?.Invoke();
        }
    }

    /// <summary>Marks the matching tool-call segment as failed.</summary>
    public void OnToolFailed(FunctionCallContent functionCall)
    {
        var seg = FindSegmentForFunction(functionCall.Name, ToolExecutionState.Executing);
        if (seg != null)
        {
            seg.ExecutionState = ToolExecutionState.Failed;
            seg.ProcessedAt = DateTimeOffset.UtcNow;
            OnSegmentChanged?.Invoke();
        }
    }

    private ChatSegment? FindSegmentForFunction(string functionName, ToolExecutionState expectedState = ToolExecutionState.Pending)
    {
        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            var seg = _segments[i];
            if (seg.Type == ChatSegmentType.ToolCall &&
                seg.FunctionCall?.Name == functionName &&
                seg.ExecutionState == expectedState)
            {
                return seg;
            }
        }

        // Check unresolved segments detached by Reset().
        for (var i = _detachedToolSegments.Count - 1; i >= 0; i--)
        {
            var seg = _detachedToolSegments[i];
            if (seg.FunctionCall?.Name == functionName && seg.ExecutionState == expectedState)
            {
                _detachedToolSegments.RemoveAt(i);
                return seg;
            }
        }

        return null;
    }

    private ChatSegment? FindSegmentForFunctionCall(string callId)
    {
        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            var seg = _segments[i];
            if (seg.Type == ChatSegmentType.ToolCall &&
                seg.FunctionCall?.CallId == callId)
            {
                return seg;
            }
        }

        return null;
    }

    private ChatSegment? FindSegmentForFunctionResult(FunctionResultContent functionResult)
    {
        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            var seg = _segments[i];
            if (seg.Type == ChatSegmentType.ToolCall &&
                seg.FunctionCall?.CallId == functionResult.CallId)
            {
                return seg;
            }
        }

        for (var i = _detachedToolSegments.Count - 1; i >= 0; i--)
        {
            var seg = _detachedToolSegments[i];
            if (seg.FunctionCall?.CallId == functionResult.CallId)
            {
                _detachedToolSegments.RemoveAt(i);
                return seg;
            }
        }

        return null;
    }

    internal void HandleFunctionCallContent(AgentResponseUpdate update, FunctionCallContent funcContent)
    {
        CloseIfDifferent(ChatSegmentType.ToolCall);

        var seg = FindSegmentForFunctionCall(funcContent.CallId);
        if (seg is null || seg.State == ChatSegmentState.Completed)
        {
            seg = CreateSegment(ChatSegmentType.ToolCall);
        }

        _openSegment = seg;
        var funcJson = JsonSerializer.Serialize(new { funcContent.Name, funcContent.Arguments });
        _openSegment.TextBuffer.Clear();
        AppendToSegment(_openSegment, update, funcJson);
        _openSegment.FunctionCall = funcContent;
        _openSegment.ExecutionState = ToolExecutionState.Pending;
        _openSegment.State = ChatSegmentState.Completed;

        _openSegment = null;
    }

    internal void HandleFunctionResultContent(AgentResponseUpdate update, FunctionResultContent funcResult)
    {
        var seg = FindSegmentForFunctionResult(funcResult);
        if (seg == null)
        {
            // AG-UI apply/default.ts tolerates content for unknown ids; ignore orphaned results rather than throwing.
            return;
        }

        seg.Updates.Add(update);
        seg.FunctionResult = funcResult;
        if (seg.ExecutionState != ToolExecutionState.Failed)
        {
            seg.ExecutionState = funcResult.Exception is null
                ? ToolExecutionState.Completed
                : ToolExecutionState.Failed;
            seg.ProcessedAt ??= DateTimeOffset.UtcNow;
        }
    }

    private void HandleReasoningContent(AgentResponseUpdate update, string text)
    {
        CloseIfDifferent(ChatSegmentType.Think);
        if (string.IsNullOrEmpty(text)) { return; }

        if (_openSegment == null ||
            _openSegment.Type != ChatSegmentType.Think ||
            _openSegment.State == ChatSegmentState.Completed)
        {
            _openSegment = CreateSegment(ChatSegmentType.Think);
        }
        else
        {
            PromoteInProgress(_openSegment);
        }

        AppendToSegment(_openSegment!, update, text);
    }

    private void HandleTextContent(AgentResponseUpdate update, string text)
    {
        if (string.IsNullOrEmpty(text)) { return; }

        // AG-UI verify.ts requires the previous reasoning/tool block to close before text content opens.
        CloseIfDifferent(ChatSegmentType.Text);

        if (_openSegment == null ||
            _openSegment.Type != ChatSegmentType.Text ||
            _openSegment.State == ChatSegmentState.Completed)
        {
            _openSegment = CreateSegment(ChatSegmentType.Text);
        }
        else
        {
            PromoteInProgress(_openSegment);
        }

        AppendToSegment(_openSegment!, update, text);
    }

    private void CloseIfDifferent(ChatSegmentType requestedType)
    {
        // One segment type is active at a time, matching AG-UI's close-before-open event ordering.
        if (_openSegment != null &&
            _openSegment.Type != requestedType &&
            _openSegment.State != ChatSegmentState.Completed)
        {
            _openSegment.State = ChatSegmentState.Completed;
            _openSegment = null;
        }
    }

    private ChatSegment CreateSegment(ChatSegmentType type)
    {
        var seg = new ChatSegment { Type = type };
        _segments.Add(seg);
        return seg;
    }

    private static void PromoteInProgress(ChatSegment seg)
    {
        if (seg.State == ChatSegmentState.Started)
        {
            seg.State = ChatSegmentState.InProgress;
        }
    }

    private static void AppendToSegment(ChatSegment seg, AgentResponseUpdate update, string text)
    {
        seg.Updates.Add(update);
        if (!string.IsNullOrEmpty(text))
        {
            seg.TextBuffer.Append(text);
        }
    }
}
