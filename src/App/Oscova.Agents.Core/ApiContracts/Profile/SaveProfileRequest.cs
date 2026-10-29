namespace Oscova.Agents.Core.ApiContracts.Profile;

/// <summary>Carries profile and optional password change fields for the current user.</summary>
public sealed class SaveProfileRequest
{
    public string FullName { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmNewPassword { get; set; } = string.Empty;
}
