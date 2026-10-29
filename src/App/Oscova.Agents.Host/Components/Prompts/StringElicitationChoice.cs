namespace Oscova.Agents.Host.Components.Prompts;

/// <summary>
/// Represents the user's response to a string elicitation request.
/// </summary>
public readonly record struct StringElicitationChoice(Guid RequestId, IReadOnlyDictionary<string, string> Values, bool IsCancel);

