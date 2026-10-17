namespace Oscova.Agents;

/// <summary>
/// Carries a toolset with the optional plugin origin used to qualify reflected tool names.
/// </summary>
public sealed record ToolsetRegistration(IAgentToolset Toolset, string? Origin = null);
