namespace Oscova.Agents.Conversations;

/// <summary>Retains a user-selected file reference with a message without copying the file or granting access.</summary>
public sealed record WorkspaceFileReference(string Name, string AgentPath, string Source);
