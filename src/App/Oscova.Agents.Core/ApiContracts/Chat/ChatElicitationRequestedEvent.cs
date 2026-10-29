using Oscova.Agents.Conversations;

namespace Oscova.Agents.Core.ApiContracts.Chat;

public sealed record ChatElicitationRequestedEvent(
    string OperationId,
    string ThreadId,
    ChatDisplayMessage Message);
