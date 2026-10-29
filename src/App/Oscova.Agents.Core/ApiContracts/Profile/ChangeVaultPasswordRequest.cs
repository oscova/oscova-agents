namespace Oscova.Agents.Core.ApiContracts.Profile;

/// <summary>Rewraps the current user's vault key with a new password.</summary>
public sealed class ChangeVaultPasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmNewPassword { get; set; } = string.Empty;
}
