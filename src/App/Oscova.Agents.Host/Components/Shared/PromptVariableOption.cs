namespace Oscova.Agents.Host.Components.Shared;

/// <summary>
/// Describes a prompt token that can be inserted into a textarea and explained in a short tooltip.
/// </summary>
public sealed record PromptVariableOption(string Token, string Description);