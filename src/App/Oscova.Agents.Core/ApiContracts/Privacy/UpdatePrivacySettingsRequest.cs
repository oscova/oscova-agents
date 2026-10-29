namespace Oscova.Agents.Core.ApiContracts.Privacy;

/// <summary>
/// Request to update the current user's privacy posture.
/// </summary>
public sealed class UpdatePrivacySettingsRequest
{
    public bool IsLocalOnly { get; set; }

    public int IdleLogoutMinutes { get; set; }
}
