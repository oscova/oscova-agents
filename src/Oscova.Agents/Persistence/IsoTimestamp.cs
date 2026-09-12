using System;
using System.Globalization;

namespace Oscova.Agents.Persistence;

/// <summary>
/// Canonical ISO 8601 UTC formatting for on-disk timestamps.
/// </summary>
public static class IsoTimestamp
{
    private const string SecondsFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const string MillisecondsFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>
    /// Formats a timestamp using UTC second precision.
    /// Use this for stable task metadata and schedule values where sub-second fidelity
    /// is unnecessary and the on-disk value should stay compact and easy to read.
    /// </summary>
    public static string FormatSeconds(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString(SecondsFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats a timestamp using UTC millisecond precision.
    /// Use this for runtime event data such as task run history where multiple updates
    /// can occur within the same second and the extra precision is useful.
    /// </summary>
    public static string FormatMilliseconds(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString(MillisecondsFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses an ISO 8601 timestamp as UTC.
    /// </summary>
    public static DateTimeOffset Parse(string value)
    {
        if (DateTimeOffset.TryParseExact(
                value,
                [SecondsFormat, MillisecondsFormat],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        throw new FormatException($"Timestamp '{value}' is not a valid ISO 8601 UTC value.");
    }
}
