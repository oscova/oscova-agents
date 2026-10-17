using System.Collections.Generic;

namespace Oscova.Agents.Plugins;

/// <summary>Describes an agent starter that a harness can copy into its own agent configuration.</summary>
public sealed record AgentTemplateDefinition
{
    /// <summary>Gets the lowercase plugin-local identifier containing ASCII letters, digits or hyphens.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the name proposed for a new agent.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the description shown before installation.</summary>
    public required string Description { get; init; }

    /// <summary>Gets the instructions copied into a new agent.</summary>
    public required string SystemInstruction { get; init; }

    /// <summary>Gets optional plain-text setup guidance shown before installation.</summary>
    public string SetupInstructions { get; init; } = string.Empty;

    /// <summary>Gets canonical tool names requested for the new agent, subject to host availability and policy.</summary>
    public IReadOnlyList<string> RequestedToolNames { get; init; } = [];

    /// <summary>Gets an optional host-owned picture key, never a file path or URL.</summary>
    public string? ProfilePictureKey { get; init; }
}
