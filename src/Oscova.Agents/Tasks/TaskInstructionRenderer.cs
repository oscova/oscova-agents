using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oscova.Agents.Tasks;

/// <summary>Renders external-trigger parameter placeholders in a task instruction.</summary>
public static class TaskInstructionRenderer
{
    private static readonly Regex PlaceholderPattern = new(
        @"\{\{parameters\.(?<name>[^{}]+)\}\}",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking);

    /// <summary>Replaces declared parameter placeholders while leaving unrelated placeholders unchanged.</summary>
    public static string Render(string instruction, IReadOnlyDictionary<string, JsonElement> parameters)
    {
        return PlaceholderPattern.Replace(
            instruction,
            match => parameters.TryGetValue(match.Groups["name"].Value, out var value)
                ? FormatValue(value)
                : match.Value);
    }

    private static string FormatValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => value.GetRawText()
        };
    }
}
