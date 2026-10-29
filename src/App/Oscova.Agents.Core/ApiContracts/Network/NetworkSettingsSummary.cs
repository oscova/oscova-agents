using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Core.ApiContracts.Network;

/// <summary>
/// Describes persisted network settings and the URLs currently detected by the web host.
/// </summary>
public sealed class NetworkSettingsSummary
{
    public bool EnableNetworkAccess { get; set; }
    public bool AllowPrivateNetworkFetch { get; set; }
    public bool AllowSandboxNetworkAccess { get; set; }
    public int Port { get; set; }
    public PublicAppUrlMode PublicAppUrlMode { get; set; }
    public string CustomPublicAppUrl { get; set; } = string.Empty;
    public string[] ActiveUrls { get; set; } = [];
    public bool IsNetworkAccessActive { get; set; }
    public int? ActivePort { get; set; }
    public string LocalUrl { get; set; } = string.Empty;
    public string? AutoNetworkUrl { get; set; }
    public string? EffectivePublicAppUrl { get; set; }
    public bool RestartRequired { get; set; }
}
