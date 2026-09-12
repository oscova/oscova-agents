namespace Oscova.Agents.Conversations;

/// <summary>Identifies who owns a persisted chat message.</summary>
public enum MessageOwner
{
    /// <summary>Identifies an assistant-authored message.</summary>
    Bot,
    /// <summary>Identifies a user-authored message.</summary>
    User
}
