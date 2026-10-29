using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Core.Logging;

public sealed record LogEntry
{
    private string? _searchText;

    [JsonPropertyName("time")]
    public string Time { get; init; } = string.Empty;

    [JsonPropertyName("level")]
    public string Level { get; init; } = string.Empty;

    [JsonPropertyName("logger")]
    public string Logger { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

    [JsonPropertyName("exception")]
    public LogEntryException? Exception { get; init; }

    [JsonIgnore]
    public bool HasException => Exception is not null
                                && !string.IsNullOrWhiteSpace(Exception.Type);

    [JsonIgnore]
    public bool HasMetadata => Metadata is { Count: > 0 };

    [JsonIgnore]
    public string? EventName => TryGetMetadataString("eventName");

    [JsonIgnore]
    public double? DurationMs => TryGetMetadataDouble("durationMs");

    [JsonIgnore]
    public string? OperationId => TryGetMetadataString("operationId");

    /// <summary>
    /// Returns the short logger name (last segment after the final dot).
    /// </summary>
    [JsonIgnore]
    public string ShortLogger
    {
        get
        {
            var idx = Logger.LastIndexOf('.');
            return idx >= 0 && idx < Logger.Length - 1
                ? Logger[(idx + 1)..]
                : Logger;
        }
    }

    /// <summary>
    /// Reads a string metadata value if the key exists and the JSON value is compatible.
    /// </summary>
    public string? TryGetMetadataString(string key)
    {
        if (Metadata is null || !Metadata.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => value.GetRawText()
        };
    }

    /// <summary>
    /// Reads a numeric metadata value even when the JSON payload wrote the number as text.
    /// </summary>
    public double? TryGetMetadataDouble(string key)
    {
        if (Metadata is null || !Metadata.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numericValue))
        {
            return numericValue;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), out var parsedValue))
        {
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Returns formatted metadata pairs for display in the log viewer.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> GetMetadataDisplayPairs()
    {
        if (Metadata is null)
        {
            yield break;
        }

        foreach (var pair in Metadata)
        {
            yield return new KeyValuePair<string, string>(pair.Key, FormatJsonElement(pair.Value));
        }
    }

    /// <summary>
    /// Returns true when the search term matches the log envelope, metadata, or exception.
    /// </summary>
    public bool MatchesSearch(string term)
    {
        return GetSearchText().Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private string GetSearchText()
    {
        _searchText ??= BuildSearchText();
        return _searchText;
    }

    private string BuildSearchText()
    {
        var parts = new List<string>(8)
        {
            Time,
            Level,
            Logger,
            Message
        };

        if (Metadata is not null)
        {
            foreach (var pair in Metadata)
            {
                parts.Add(pair.Key);
                parts.Add(FormatJsonElement(pair.Value));
            }
        }

        if (Exception is not null)
        {
            parts.Add(Exception.Type);
            parts.Add(Exception.Message);
            parts.Add(Exception.Stacktrace);
        }

        return string.Join('\n', parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string FormatJsonElement(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Null => "null",
            _ => value.GetRawText()
        };
    }
}

public sealed record LogEntryException
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("stacktrace")]
    public string Stacktrace { get; init; } = string.Empty;
}
