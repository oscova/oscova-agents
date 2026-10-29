namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Identifies the active chat run the caller intends to stop.</summary>
public sealed class StopChatRunRequest
{
    public required string RunId { get; init; }
}
