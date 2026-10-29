using System.Text.Json;

namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Represents a numeric elicitation value with an integer-mode flag.</summary>
public sealed class NumberElicitationValue
{
    public JsonElement Value { get; set; }

    public bool IsInteger { get; set; }
}
