namespace Oscova.Agents.Policies;

/// <summary>
/// Describes how a tool relates to the network, used to enforce offline mode.
/// </summary>
public enum NetworkScope
{
    /// <summary>The tool does not use the network.</summary>
    None,

    /// <summary>The tool is local work that may reach the network as a side effect.</summary>
    Incidental,

    /// <summary>The tool's purpose is outbound network access.</summary>
    Egress
}
