using System;

namespace Oscova.Agents.Definitions;

/// <summary>Reports that two agent names resolve to the same callable tool name.</summary>
public sealed class AgentCallableNameCollisionException : InvalidOperationException
{
    /// <summary>Creates a callable-name collision with optional internal conflict detail.</summary>
    public AgentCallableNameCollisionException(
        string agentName,
        string callableName,
        string? conflictingAgentName = null)
        : base(BuildMessage(agentName, callableName, conflictingAgentName))
    {
        AgentName = agentName;
        CallableName = callableName;
        ConflictingAgentName = conflictingAgentName;
    }

    /// <summary>Gets the rejected agent display name.</summary>
    public string AgentName { get; }

    /// <summary>Gets the normalized callable name that collided.</summary>
    public string CallableName { get; }

    /// <summary>Gets the conflicting display name when it is safe and useful internally.</summary>
    public string? ConflictingAgentName { get; }

    private static string BuildMessage(string agentName, string callableName, string? conflictingAgentName)
    {
        var conflict = string.IsNullOrWhiteSpace(conflictingAgentName)
            ? "another agent"
            : $"\"{conflictingAgentName}\"";
        return $"The agent name \"{agentName}\" resolves to callable name \"{callableName}\", " +
               $"which is already used by {conflict}. Choose a different agent name.";
    }
}
