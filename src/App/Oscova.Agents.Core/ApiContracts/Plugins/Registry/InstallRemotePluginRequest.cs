namespace Oscova.Agents.Core.ApiContracts.Plugins.Registry;

/// <summary>
/// Targets one NuGet package for installation into the system plugin directory.
/// </summary>
public sealed class InstallRemotePluginRequest
{
    public string? ReplacePluginId { get; set; }
    public string? ReplacePackageId { get; set; }
    public string PackageId { get; set; } = string.Empty;
}
