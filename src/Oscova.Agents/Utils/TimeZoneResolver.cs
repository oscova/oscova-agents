using System;
using TimeZoneConverter;

namespace Oscova.Agents.Utils;

/// <summary>
/// Resolves user-supplied time-zone identifiers across Windows and IANA naming schemes.
/// </summary>
public static class TimeZoneResolver
{
    /// <summary>
    /// Resolves a time-zone identifier or falls back to the local system time zone.
    /// </summary>
    /// <param name="timeZoneId">A Windows or IANA time-zone identifier.</param>
    /// <returns>The resolved time zone, or <see cref="TimeZoneInfo.Local"/> when the identifier is blank or invalid.</returns>
    public static TimeZoneInfo ResolveOrLocal(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId) &&
            TZConvert.TryGetTimeZoneInfo(timeZoneId, out var timeZone) &&
            timeZone is not null)
        {
            return timeZone;
        }

        return TimeZoneInfo.Local;
    }

    /// <summary>
    /// Normalizes a time-zone identifier to the platform-resolved identifier.
    /// </summary>
    /// <param name="timeZoneId">A Windows or IANA time-zone identifier.</param>
    /// <returns>The resolved identifier, or an empty string when the input is blank or invalid.</returns>
    public static string NormalizeOrEmpty(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId) &&
            TZConvert.TryGetTimeZoneInfo(timeZoneId, out var timeZone) &&
            timeZone is not null)
        {
            return timeZone.Id;
        }

        return string.Empty;
    }
}
