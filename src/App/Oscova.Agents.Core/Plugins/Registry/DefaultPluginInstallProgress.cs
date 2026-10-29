namespace Oscova.Agents.Core.Plugins.Registry;

/// <summary>Reports one default component's startup installation state.</summary>
public sealed record DefaultPluginInstallProgress(
    string PackageId, int Number, int Total, DefaultPluginInstallState State);

/// <summary>Distinguishes an active installation from its successful or failed outcome.</summary>
public enum DefaultPluginInstallState
{
    Installing,
    Installed,
    Failed
}
