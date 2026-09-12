namespace Oscova.Agents.FileBackups;

/// <summary>
/// Fixed retention guardrails for lightweight user file history.
/// </summary>
public static class UserFileBackupLimits
{
    /// <summary>Gets the maximum number of restore points retained per source file.</summary>
    public const int MaxVersionsPerFile = 30;
}
