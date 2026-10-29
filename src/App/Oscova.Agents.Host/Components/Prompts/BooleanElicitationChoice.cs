namespace Oscova.Agents.Host.Components.Prompts;

/// <summary>
/// Represents the user's decision in response to a boolean elicitation request.
/// </summary>
public readonly record struct BooleanElicitationChoice(Guid RequestId, bool? Value, bool IsCancel);

