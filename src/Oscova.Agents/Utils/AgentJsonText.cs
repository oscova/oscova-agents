using System;
using System.Text.Json;

namespace Oscova.Agents.Utils;

/// <summary>
/// Renders tool payload values as text so history summaries and prompt-fit checks use the same JSON shape.
/// </summary>
public static class AgentJsonText
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Converts a payload value to readable JSON text, preserving plain strings and falling back safely.
    /// </summary>
    public static string SerializeValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is string text)
        {
            return text;
        }

        try
        {
            return JsonSerializer.Serialize(value, JsonOptions);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            // Keep non-serializable payload text so prompt rendering and accounting paths keep moving.
            return value.ToString() ?? string.Empty;
        }
    }
}
