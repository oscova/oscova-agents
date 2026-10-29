namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Identifies the run that accepted a follow-up, or reports that it remains queued.</summary>
public sealed record ChatFollowUpResult(ChatFollowUpDisposition Disposition, string? RunId);
