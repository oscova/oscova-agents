namespace Oscova.Agents.Core.ApiContracts.Updates;

/// <summary>Names the exact application release to update to.</summary>
public sealed record StartApplicationUpdateRequest(string TargetVersion);

/// <summary>Identifies one newer installed package release admitted by the target framework.</summary>
public sealed record InstalledPluginUpdate(string PluginId, string Name, string PackageId, string InstalledVersion, string Version);

/// <summary>Reports the retained, updated or disabled state of an installed plugin after upgrade.</summary>
public sealed record PluginUpdateOutcome(string PluginId, string Name, string State, string? Version, string? Reason);

/// <summary>Reports release availability and the current update operation.</summary>
public sealed record ApplicationUpdateStatus(
    string CurrentVersion,
    string? AvailableVersion,
    bool CanUpdate,
    string State,
    string? ReasonCode,
    string? OperationId,
    string? BackupDirectory)
{
    public IReadOnlyList<InstalledPluginUpdate> PluginUpdates { get; init; } = [];
    public IReadOnlyList<PluginUpdateOutcome> PluginOutcomes { get; init; } = [];
    public bool CompletionAcknowledged { get; init; }
    /// <summary>True when the finished attempt kept the application version and updated only plugins.</summary>
    public bool PluginsOnly { get; init; }
    public string? Diagnostic { get; init; }
}
