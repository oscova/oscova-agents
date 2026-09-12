namespace Oscova.Agents.Conversations;

/// <summary>Describes the processing state of a chat segment.</summary>
public enum ChatSegmentState
{
    /// <summary>Indicates that segment processing has started.</summary>
    Started,
    /// <summary>Indicates that segment processing is in progress.</summary>
    InProgress,
    /// <summary>Indicates that segment processing has completed.</summary>
    Completed
}
