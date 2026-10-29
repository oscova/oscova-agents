namespace Oscova.Agents.Core.ApiContracts.Execution;

/// <summary>Requests the operator's execution selection with explicit interruption consent.</summary>
public sealed record ExecutionSettingsRequest(string? ProviderId, bool ConfirmInterruptions = false);
