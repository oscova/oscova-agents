namespace Oscova.Agents.Core.ApiContracts.Common;

/// <summary>Returned by operations that succeed with a human-readable message.</summary>
public sealed class SuccessResult
{
    public string Message { get; set; } = string.Empty;

    /// <summary>Indicates that the operation completed with unresolved cleanup or other warnings.</summary>
    public bool HasWarnings { get; set; }
}
