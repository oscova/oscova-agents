namespace Oscova.Agents.Core.ApiContracts.Admin;

/// <summary>Carries the new password for a user password reset.</summary>
public sealed class ResetPasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;
}
