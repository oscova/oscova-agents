namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Reports what the server did with a follow-up message.</summary>
public enum ChatFollowUpDisposition
{
    Steered,
    Queued,
    Started
}
