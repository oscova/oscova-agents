namespace Oscova.Agents.Conversations;

/// <summary>Identifies the content represented by a chat segment.</summary>
public enum ChatSegmentType
{
    /// <summary>Represents model reasoning content.</summary>
    Think,
    /// <summary>Represents a tool call and its result.</summary>
    ToolCall,
    /// <summary>Represents visible text content.</summary>
    Text
}
