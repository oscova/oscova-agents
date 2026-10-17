namespace Oscova.Agents.Plugins;

/// <summary>Supplies named default guidance without owning user customization or storage.</summary>
public sealed record AgentInstructionContribution(string Key, string Label, string DefaultInstruction);
