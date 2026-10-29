namespace Oscova.Agents.Core.Projects;

/// <summary>Identifies a missing or filesystem-root project folder with a safe message for project callers and chat viewers.</summary>
public sealed class ProjectFolderUnavailableException() : ArgumentException("Project folder must exist and cannot be a filesystem root.");
