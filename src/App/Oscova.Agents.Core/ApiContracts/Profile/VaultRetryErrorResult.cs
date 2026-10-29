namespace Oscova.Agents.Core.ApiContracts.Profile;

/// <summary>Carries the server's vault retry deadline across the API boundary.</summary>
public sealed class VaultRetryErrorResult
{
    public string Message { get; set; } = string.Empty;
    public required DateTimeOffset RetryUntil { get; set; }
}
