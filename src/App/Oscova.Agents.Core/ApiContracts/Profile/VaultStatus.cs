namespace Oscova.Agents.Core.ApiContracts.Profile;

/// <summary>Reports the user's vault state and whether shared operator configuration is available.</summary>
public sealed class VaultStatus
{
    public bool IsConfigured { get; set; }
    public bool IsUnlocked { get; set; }
    public bool IsOperatorReady { get; set; }
    public DateTimeOffset? RetryUntil { get; set; }
}
