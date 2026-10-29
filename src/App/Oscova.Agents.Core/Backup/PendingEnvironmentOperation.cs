namespace Oscova.Agents.Core.Backup;

/// <summary>
/// Describes the pending environment operation the next startup must apply.
/// </summary>
public enum EnvironmentOperationMode
{
    Restore,
    Reset
}

/// <summary>
/// Records the durable phase of an environment replacement or recovery.
/// </summary>
public enum EnvironmentRestorePhase
{
    Prepared,
    MovingPrevious,
    MovingStaged,
    AwaitingHealth,
    Committed,
    RecoveringStaged,
    RecoveringPrevious
}

/// <summary>
/// Startup marker for a staged environment replacement.
/// </summary>
public sealed class PendingEnvironmentOperation
{
    public const string FileName = "operation.json";

    public EnvironmentOperationMode Mode { get; set; } = EnvironmentOperationMode.Restore;

    public int InitiatingProcessId { get; set; }

    public EnvironmentBundleManifest? Manifest { get; set; }

    public string? ResetOperationId { get; set; }

    public DateTimeOffset? ResetCreatedAtUtc { get; set; }

    public IReadOnlyList<string>? ResetRestartArguments { get; set; }

    public EnvironmentRestorePhase Phase { get; set; }

    public IReadOnlyList<string> PreviousEntries { get; set; } = [];

    public IReadOnlyList<string> StagedEntries { get; set; } = [];
}
