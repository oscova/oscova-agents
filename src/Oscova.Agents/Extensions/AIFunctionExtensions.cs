using Microsoft.Extensions.AI;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Oscova.Agents.Extensions;

/// <summary>
/// Formats AI function schemas into prompt-friendly text summaries.
/// </summary>
public static class AIFunctionExtensions
{
    /// <summary>
    /// Builds a text summary for a collection of tools.
    /// </summary>
    /// <param name="tools">The tools to summarize.</param>
    /// <returns>A newline-delimited summary of each tool schema.</returns>
    public static string GetToolsSummary(this IEnumerable<AIFunction> tools)
    {
        var sb = new StringBuilder();
        foreach (var tool in tools)
        {
            sb.AppendLine(tool.GetSchemaSummary());
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds a text summary of one tool's name, description, and JSON-schema parameters.
    /// </summary>
    /// <param name="function">The AI function to summarize.</param>
    /// <returns>A prompt-ready schema summary.</returns>
    public static string GetSchemaSummary(this AIFunction function)
    {
        var sb = new StringBuilder();
        var jsonSchema = function.JsonSchema;

        sb.AppendLine($"Tool: {function.Name}");
        if (jsonSchema.TryGetProperty("description", out var toolDescription))
        {
            sb.AppendLine($"Description: {toolDescription.GetString()}");
        }

        if (jsonSchema.TryGetProperty("properties", out var properties))
        {
            sb.AppendLine("Arguments:");
            foreach (var property in properties.EnumerateObject())
            {
                var paramName = property.Name;
                var paramInfo = property.Value;

                var description = paramInfo.TryGetProperty("description", out var paramDesc)
                    ? $": {paramDesc.GetString()} "
                    : string.Empty;

                var type = paramInfo.TryGetProperty("type", out var paramType)
                    ? paramType.ToString()
                    : "Unknown type";

                var isRequired = jsonSchema.TryGetProperty("required", out var required) &&
                                 required.EnumerateArray().Any(r => r.GetString() == paramName);

                sb.AppendLine(
                    $"- {paramName} ({type}, {(isRequired ? "required" : "optional")})" +
                    $"{(string.IsNullOrWhiteSpace(description) ? "" : $": {description}")}");
            }
        }

        return sb.ToString();
    }
}
