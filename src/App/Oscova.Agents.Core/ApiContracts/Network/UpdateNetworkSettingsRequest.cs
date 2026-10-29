using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Core.ApiContracts.Network;

/// <summary>
/// Saves Admin-controlled network access and public app URL settings.
/// </summary>
public sealed class UpdateNetworkSettingsRequest
{
    public bool ConfirmInterruptions { get; set; }
    public bool EnableNetworkAccess { get; set; }
    public bool AllowPrivateNetworkFetch { get; set; }
    public bool AllowSandboxNetworkAccess { get; set; }
    public int Port { get; set; } = NetworkSettings.DefaultPort;
    public PublicAppUrlMode PublicAppUrlMode { get; set; } = PublicAppUrlMode.AutoNetwork;
    public string CustomPublicAppUrl { get; set; } = string.Empty;
}
