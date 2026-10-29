namespace Oscova.Agents.Core.ApiContracts.Admin;

/// <summary>Carries updated profile fields for a specific user (admin operation).</summary>
public sealed class SaveUserProfileRequest
{
    public string UserName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
