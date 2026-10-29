using Oscova.Agents.Conversations;
using System.Collections;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Host.Components.Chat.ToolCards;

/// <summary>
/// Carries shared tool-call display data for conversation and Activity rows.
/// </summary>
public sealed class ToolCardContext
{
    /// <summary>Builds the shared summary without formatting retained output.</summary>
    public ToolCardContext(ChatSegment segment, ToolCardDescriptor descriptor)
    {
        Segment = segment;
        Descriptor = descriptor;
        FunctionCall = segment.FunctionCall;
        Arguments = FunctionCall?.Arguments
            ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        Summary = descriptor.BuildSummary(Arguments);
    }

    public ChatSegment Segment { get; }

    public FunctionCallContent? FunctionCall { get; }

    public ToolCardDescriptor Descriptor { get; }

    public IDictionary<string, object?> Arguments { get; }

    public string Summary { get; }

    public string? ResultText => Segment.FunctionResult?.Result?.ToString();

    /// <summary>Formats a retained argument for display in technical details.</summary>
    public static string FormatValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is string text)
        {
            return text;
        }

        if (value is IEnumerable enumerable and not IDictionary)
        {
            return string.Join(", ", enumerable.Cast<object?>().Select(static item => item?.ToString() ?? "null"));
        }

        return value.ToString() ?? string.Empty;
    }
}
