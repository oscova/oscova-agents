namespace Oscova.Agents.Plugins;

/// <summary>
/// Severity for a plugin loading diagnostic.
/// </summary>
public enum AgentPluginLoadDiagnosticLevel
{
    /// <summary>
    /// Indicates a recoverable plugin loading issue.
    /// </summary>
    Warning,

    /// <summary>
    /// Indicates a plugin loading issue that prevented the plugin from loading.
    /// </summary>
    Error
}

/// <summary>
/// Human-readable plugin loading diagnostic returned to the host for logging or tests.
/// </summary>
/// <param name="Level">The severity of the loading issue.</param>
/// <param name="Path">The manifest or directory path associated with the diagnostic.</param>
/// <param name="Message">The human-readable diagnostic message.</param>
public sealed record AgentPluginLoadDiagnostic(
    AgentPluginLoadDiagnosticLevel Level,
    string Path,
    string Message);