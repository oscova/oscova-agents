using Oscova.Agents.Conversations;

namespace Oscova.Agents.Core.ApiContracts.Chat;

public sealed record ChatToolApprovalRequestedEvent(
    string OperationId,
    string ThreadId,
    ChatDisplayMessage Message);
