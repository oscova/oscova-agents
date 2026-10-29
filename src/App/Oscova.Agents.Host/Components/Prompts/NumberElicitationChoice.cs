using System.Text.Json;
namespace Oscova.Agents.Host.Components.Prompts;

/// <summary>
/// Represents a numeric response collected from the user.
/// </summary>
public readonly record struct NumberElicitationChoice(Guid RequestId, IReadOnlyDictionary<string, NumberElicitationValue> Values, bool IsCancel);

/// <summary>
/// Captures the numeric value returned from the elicitation UI.
/// </summary>
public readonly record struct NumberElicitationValue(JsonElement Value, bool IsInteger);

