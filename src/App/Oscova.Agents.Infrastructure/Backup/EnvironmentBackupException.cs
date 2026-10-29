namespace Oscova.Agents.Infrastructure.Backup;

/// <summary>
/// Represents a user-correctable backup or restore validation failure.
/// </summary>
public sealed class EnvironmentBackupException : Exception
{
    /// <summary>
    /// Creates a validation failure with a user-safe message.
    /// </summary>
    public EnvironmentBackupException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates a validation failure that preserves the low-level cause for logs.
    /// </summary>
    public EnvironmentBackupException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
