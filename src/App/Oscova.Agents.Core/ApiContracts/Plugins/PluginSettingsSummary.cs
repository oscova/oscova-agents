using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Core.ApiContracts.Plugins;

/// <summary>
/// Describes whether a plugin is active in the current process or unable to load.
/// </summary>
public enum PluginRuntimeStatus
{
    Active,
    Inactive,
    LoadFailed,
    InvalidManifest
}

/// <summary>
/// Presents one installed plugin manifest and its activation state to administrators.
/// </summary>
public sealed class PluginSettingsEntry
{
    public NodePluginStatus? Devices { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? PendingName { get; set; }
    public string? PendingVersion { get; set; }
    public PluginHardwareRequirement? HardwareRequirement { get; set; }
    public PluginHardwareRequirement? PendingHardwareRequirement { get; set; }
    public string? Assembly { get; set; }
    public string? EntryType { get; set; }
    public PluginRuntimeStatus Status { get; set; }
    public bool CanConfigure { get; set; }
    public bool LoadOnNextRestart { get; set; }
    public bool EnabledAtStartup { get; set; }
    public string? DiagnosticMessage { get; set; }
}

/// <summary>
/// Returns installed plugin rows and whether activation changes or pending replacements require a restart.
/// </summary>
public sealed class PluginSettingsSummary
{
    public IReadOnlyList<PluginSettingsEntry> Plugins { get; set; } = [];
    public bool RestartRequired { get; set; }
}
