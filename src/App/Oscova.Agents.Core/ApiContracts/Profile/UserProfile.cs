namespace Oscova.Agents.Core.ApiContracts.Profile;

/// <summary>The current user's profile information.</summary>
public sealed class UserProfile
{
    public string UserName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
