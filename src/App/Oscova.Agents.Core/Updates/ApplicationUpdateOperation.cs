using Oscova.Agents.Core.ApiContracts.Updates;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Core.Updates;

/// <summary>Describes the verified tool installation used for an application update.</summary>
public sealed record ApplicationToolIdentity(
    string PackageVersion,
    string DotnetPath,
    string GlobalToolsPath,
    string AppDataPath,
    string UpdatesDirectory,
    string WorkingDirectory,
    IReadOnlyList<string> RestartArguments);

/// <summary>
/// Records how far an update attempt got. A successful attempt moves Started, CheckpointVerified,
/// ChangesBegun, Completed. Started is written before the checkpoint so a crash during the backup
/// still leaves evidence that the original data was never changed.
/// </summary>
public enum ApplicationUpdateState
{
    Started,
    CheckpointVerified,
    ChangesBegun,
    Completed,
    Failed,
    Restoring,
    Restored
}

/// <summary>Persists one application update attempt outside the data root.</summary>
public sealed class ApplicationUpdateOperation
{
    public const string FileName = "operation.json";

    public string OperationId { get; set; } = string.Empty;
    public string SourceVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public IReadOnlyList<PluginUpdateOutcome> PluginOutcomes { get; set; } = [];
    public bool CompletionAcknowledged { get; set; }
    public int DataFormatVersion { get; set; }
    public string AppDataPath { get; set; } = string.Empty;
    public string UpdatesDirectory { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public ApplicationUpdateState State { get; set; }

    /// <summary>The state the attempt was in when it failed.</summary>
    public ApplicationUpdateState? FailedPhase { get; set; }

    public string? Diagnostic { get; set; }

    /// <summary>True when this attempt is neither completed nor restored.</summary>
    [JsonIgnore]
    public bool IsUnfinished => State is not (ApplicationUpdateState.Completed or ApplicationUpdateState.Restored);

    /// <summary>True when the original data may have been changed, so only the checkpoint can restore it.</summary>
    [JsonIgnore]
    public bool ChangesBegunReached => State is ApplicationUpdateState.ChangesBegun or ApplicationUpdateState.Restoring ||
        State == ApplicationUpdateState.Failed && FailedPhase == ApplicationUpdateState.ChangesBegun;
}
