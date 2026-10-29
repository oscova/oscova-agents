namespace Oscova.Agents.Core.ApiContracts.Profile;

/// <summary>Creates the current user's Vault Password.</summary>
public sealed class SetVaultPasswordRequest
{
    public string Password { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}
