namespace Oscova.Agents.Core.Utils;

public static class DateTimeExtensions
{
    /// <summary>
    /// Converts a UTC <see cref="DateTime"/> to the specified timezone.
    /// </summary>
    public static DateTime ToUserTime(this DateTime utcDateTime, TimeZoneInfo timeZone)
    {
        return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, timeZone);
    }

    /// <summary>
    /// Converts a <see cref="DateTimeOffset"/> to the specified timezone.
    /// </summary>
    public static DateTimeOffset ToUserTime(this DateTimeOffset dateTime, TimeZoneInfo timeZone)
    {
        return TimeZoneInfo.ConvertTime(dateTime, timeZone);
    }
}
