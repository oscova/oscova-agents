namespace Oscova.Agents.Core.ApiContracts.Common;

/// <summary>Returned by operations that fail with a human-readable message.</summary>
public sealed class ErrorResult
{
    public string Message { get; set; } = string.Empty;
    public bool RequiresInterruptionConfirmation { get; set; }
}
