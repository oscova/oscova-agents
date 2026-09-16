namespace Oscova.Agents.Workspace;

/// <summary>Describes a granted directory and its optional canonical agent-facing root.</summary>
public sealed record AllowedDirectory(
    string Value,
    string Label,
    DirectorySourceKind Kind,
    WorkspaceAccessMode AccessMode,
    string? AgentPath = null);

/// <summary>
/// Identifies how an allowed directory is sourced by workspace and external-directory providers.
/// </summary>
public enum DirectorySourceKind
{
    /// <summary>
    /// A local filesystem path resolved by the host application.
    /// </summary>
    LocalPath,

    /// <summary>
    /// A folder path stored relative to the current user's app-data root.
    /// </summary>
    UserDirectory
}

/// <summary>
/// Describes the permission granted to a resolved allowed directory.
/// </summary>
public enum WorkspaceAccessMode
{
    /// <summary>
    /// Allows tools to read files and directories but rejects mutating path parameters.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// Allows tools to read and write files beneath the directory.
    /// </summary>
    ReadWrite
}

/// <summary>
/// Describes whether a tool parameter is read-only or mutating when validating workspace paths.
/// </summary>
public enum WorkspacePathAccess
{
    /// <summary>
    /// The tool parameter only reads from the path.
    /// </summary>
    Read,

    /// <summary>
    /// The tool parameter may create, update, delete, or move content at the path.
    /// </summary>
    Write
}
