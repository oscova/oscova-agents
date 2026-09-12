using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Oscova.Agents.Utils;

/// <summary>
/// Redacts sensitive values from tool-call arguments before they are shown outside the execution pipeline.
/// </summary>
public static class ToolCallArgumentRedactor
{
    private static readonly HashSet<string> SecretArgumentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "secret", "token", "api_key", "apikey", "authorization", "credential"
    };

    /// <summary>
    /// Returns a redacted copy of the supplied tool-call arguments.
    /// </summary>
    public static IDictionary<string, object?> Redact(IDictionary<string, object?> arguments)
    {
        var redacted = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
        foreach (var pair in arguments)
        {
            redacted[pair.Key] = RedactValue(pair.Key, pair.Value);
        }
        return redacted;
    }

    private static object? RedactValue(string? key, object? value)
    {
        if (!string.IsNullOrWhiteSpace(key) && IsSecretKey(key))
        {
            return "***";
        }

        return value switch
        {
            null => null,
            JsonElement element => RedactJsonElement(element),
            IDictionary<string, object?> dictionary => Redact(dictionary),
            IDictionary dictionary => RedactDictionary(dictionary),
            string => value,
            IEnumerable enumerable => RedactEnumerable(enumerable),
            _ when IsScalarValue(value) => value,
            _ => RedactJsonElement(JsonSerializer.SerializeToElement(value))
        };
    }

    private static bool IsScalarValue(object value)
    {
        return value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
            or DateTime or DateTimeOffset or Guid or Uri;
    }

    private static Dictionary<string, object?> RedactDictionary(IDictionary dictionary)
    {
        var redacted = new Dictionary<string, object?>(dictionary.Count, StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            var key = entry.Key?.ToString() ?? string.Empty;
            redacted[key] = RedactValue(key, entry.Value);
        }
        return redacted;
    }

    private static List<object?> RedactEnumerable(IEnumerable enumerable)
    {
        var redacted = new List<object?>();
        foreach (var item in enumerable)
        {
            redacted.Add(RedactValue(null, item));
        }
        return redacted;
    }

    private static object? RedactJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => RedactJsonObject(element),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(static item => RedactJsonElement(item))
                .ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue
                : element.TryGetDouble(out var doubleValue)
                    ? doubleValue
                    : element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private static Dictionary<string, object?> RedactJsonObject(JsonElement element)
    {
        var redacted = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            redacted[property.Name] = RedactValue(property.Name, property.Value);
        }
        return redacted;
    }

    private static bool IsSecretKey(string key)
    {
        foreach (var marker in SecretArgumentKeys)
        {
            if (key.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
