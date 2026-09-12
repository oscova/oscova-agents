using System;

namespace Oscova.Agents.Policies;

/// <summary>
/// Declares a reflected tool method's relationship to the network for offline-mode enforcement.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolNetworkScopeAttribute(NetworkScope scope) : Attribute
{
    /// <summary>The declared network scope for the tool method.</summary>
    public NetworkScope Scope { get; } = scope;
}
