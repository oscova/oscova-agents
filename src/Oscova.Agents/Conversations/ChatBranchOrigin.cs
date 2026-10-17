namespace Oscova.Agents.Conversations;

/// <summary>Identifies a copied conversation boundary and its source family.</summary>
public sealed record ChatBranchOrigin(
    string ParentThreadId,
    string MessageId,
    string UserMessageId,
    string RootThreadId,
    string RootTitle,
    int Number);
