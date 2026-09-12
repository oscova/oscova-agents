using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Conversations;

/// <summary>Represents one parsed segment of a streamed chat response.</summary>
public sealed class ChatSegment
{
    /// <summary>Initializes a new segment in the started state.</summary>
    public ChatSegment()
    {
        CreatedAt = DateTimeOffset.UtcNow;
        State = ChatSegmentState.Started;
    }

    // Used by System.Text.Json to rehydrate without triggering side-effects.
    /// <summary>Rehydrates a persisted chat segment without streaming side effects.</summary>
    [JsonConstructor]
    public ChatSegment(
        ChatSegmentType type,
        ChatSegmentState state,
        DateTimeOffset createdAt,
        DateTimeOffset? processedAt,
        string? text,
        FunctionCallContent? functionCall,
        FunctionResultContent? functionResult = null,
        ToolExecutionState executionState = ToolExecutionState.None
    )
    {
        // Assign in a side-effect-free way.
        CreatedAt = createdAt;
        _state = state; // Avoid setter to prevent ProcessedAt being set to UtcNow during deserialization.
        ProcessedAt = processedAt;

        Type = type;
        FunctionCall = functionCall;
        FunctionResult = functionResult;
        ExecutionState = executionState;

        if (!string.IsNullOrEmpty(text))
        {
            TextBuffer.Append(text);
        }
    }

    /// <summary>Gets or sets the segment content type.</summary>
    public ChatSegmentType Type { get; set; }

    private ChatSegmentState _state;
    /// <summary>Gets or sets the segment processing state.</summary>
    public ChatSegmentState State
    {
        get => _state;
        set
        {
            _state = value;
            if (_state == ChatSegmentState.Completed && ProcessedAt is null)
            {
                ProcessedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>Gets when the segment was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets or sets when processing completed.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Gets the elapsed segment duration.</summary>
    [JsonIgnore]
    public TimeSpan Duration => ProcessedAt.HasValue
        ? ProcessedAt.Value - CreatedAt
        : TimeSpan.Zero;

    /// <summary>Gets the streaming updates that contributed to this segment.</summary>
    [JsonIgnore]
    public IList<AgentResponseUpdate> Updates { get; } = [];

    /// <summary>Gets the aggregate text buffer for the segment.</summary>
    [JsonIgnore]
    public StringBuilder TextBuffer { get; } = new();

    /// <summary>Gets the flattened segment text.</summary>
    public string Text => TextBuffer.ToString();

    /// <summary>Gets the parsed function call when present.</summary>
    public FunctionCallContent? FunctionCall { get; internal set; }

    /// <summary>Gets the parsed function result matched to <see cref="FunctionCall"/>.</summary>
    public FunctionResultContent? FunctionResult { get; internal set; }

    /// <summary>Gets or sets the tool execution state independently of streaming <see cref="State"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ToolExecutionState ExecutionState { get; set; }
}
