using System;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Toolsets;

/// <summary>
/// Marks a tool parameter as a workspace path so agent-level directory policy can validate it.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class WorkspacePathAttribute : Attribute
{
    /// <summary>
    /// Gets the read or write access required for the annotated path parameter.
    /// </summary>
    public WorkspacePathAccess Access { get; init; } = WorkspacePathAccess.Read;
}
