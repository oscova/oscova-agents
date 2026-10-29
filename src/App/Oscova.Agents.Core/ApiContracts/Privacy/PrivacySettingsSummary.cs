namespace Oscova.Agents.Core.ApiContracts.Privacy;

/// <summary>
/// Current per-user privacy settings plus read-only effect details for the settings page.
/// </summary>
public sealed class PrivacySettingsSummary
{
    public bool IsLocalOnly { get; set; }

    public int IdleLogoutMinutes { get; set; }

    public int DisabledNetworkToolCount { get; set; }

    public IReadOnlyList<string> CloudProviderNames { get; set; } = [];
}
