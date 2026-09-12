using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Oscova.Agents.Utils;

/// <summary>
/// Converts JSON tool-call argument payloads into Microsoft.Extensions.AI function arguments.
/// </summary>
public static class AIFunctionArgumentsExtensions
{
    /// <summary>
    /// Converts an optional JSON object into function arguments.
    /// </summary>
    /// <param name="arguments">The JSON object supplied by an external tool call.</param>
    /// <returns>A function argument bag; non-object or missing payloads return an empty bag.</returns>
    public static AIFunctionArguments ToFunctionArguments(this JsonElement? arguments)
    {
        var result = new AIFunctionArguments();
        if (arguments is not { ValueKind: JsonValueKind.Object } element)
        {
            return result;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            result[property.Name] = property.Value;
        }

        return result;
    }
}
