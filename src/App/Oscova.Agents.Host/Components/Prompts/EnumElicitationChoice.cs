namespace Oscova.Agents.Host.Components.Prompts;

/// <summary>
/// Represents the user's selection from an enum elicitation schema.
/// </summary>
public readonly record struct EnumElicitationChoice(Guid RequestId, IReadOnlyDictionary<string, string> Values, bool IsCancel);

