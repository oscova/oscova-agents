using Oscova.Agents.Conversations;

namespace Oscova.Agents.Host.Server.Agents;

/// <summary>Captures one settled web reply's visible, native and file state.</summary>
internal sealed record ChatTurnCheckpoint(
    ChatThread Thread,
    IReadOnlyList<string> ToolOutputFileNames);
