namespace Oscova.Agents.Core.ApiContracts.Profile;

/// <summary>Unlocks the current user's vault for this Host process.</summary>
public sealed class UnlockVaultRequest
{
    public string Password { get; set; } = string.Empty;
}
