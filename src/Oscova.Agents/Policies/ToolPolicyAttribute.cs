using System;

namespace Oscova.Agents.Policies;

/// <summary>
/// Declares the default governance policy for a reflected tool method.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolPolicyAttribute : Attribute
{
    /// <summary>
    /// Gets the default approval mode declared for the reflected tool method.
    /// </summary>
    public ToolApprovalMode ApprovalMode { get; init; } = ToolApprovalMode.Default;
}
