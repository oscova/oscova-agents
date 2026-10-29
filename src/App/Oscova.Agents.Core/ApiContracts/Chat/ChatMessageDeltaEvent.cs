using Oscova.Agents.Conversations;

namespace Oscova.Agents.Core.ApiContracts.Chat;

public sealed record ChatMessageDeltaEvent(
    string OperationId,
    string ThreadId,
    ChatDisplayMessage Message,
    bool IsFinal);
