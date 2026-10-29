using System.ComponentModel;
using System.Globalization;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Utils;
using Oscova.Agents.Harness;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>
/// Supplies current date/time and timezone conversion tools so agents do not need shell access for clock reads.
/// </summary>
public sealed class DateTimeToolset(AssistantUiSettings uiSettings) : IAgentToolset
{
    /// <summary>
    /// Returns the current date and time in the user's timezone or in a supplied timezone.
    /// </summary>
    [DisplayName("get_current")]
    [AgentTool]
    [Description("Returns the current date and time in the user's timezone, or in a specified timezone.")]
    public string GetCurrentTime(
        [Description("Optional IANA or Windows timezone id (e.g. 'America/New_York'). Defaults to the user's configured timezone.")]
        string? timezone = null)
    {
        var zone = string.IsNullOrWhiteSpace(timezone)
            ? uiSettings.GetTimeZone()
            : ResolveTimeZone(timezone);
        var now = DateTimeOffset.UtcNow.ToUserTime(zone);

        return string.Create(CultureInfo.InvariantCulture,
            $"{now:yyyy-MM-dd HH:mm:ss} {now:dddd} ({zone.Id}, UTC{now:zzz})");
    }

    /// <summary>
    /// Converts a 24-hour time of day from one timezone to another using today's source-zone date.
    /// </summary>
    [DisplayName("convert")]
    [AgentTool]
    [Description("Converts a time of day from one timezone to another. Assumes today's date.")]
    public string ConvertTime(
        [Description("The time to convert, 24-hour 'HH:mm'.")] string time,
        [Description("Target IANA or Windows timezone id.")] string target_timezone,
        [Description("Source IANA or Windows timezone id. Defaults to the user's configured timezone.")]
        string? source_timezone = null)
    {
        if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new ToolFailureException("The time is invalid.", "Use 24-hour HH:mm format.");
        }

        if (string.IsNullOrWhiteSpace(target_timezone))
        {
            throw new ToolFailureException("A target timezone is required.");
        }

        var sourceZone = string.IsNullOrWhiteSpace(source_timezone)
            ? uiSettings.GetTimeZone()
            : ResolveTimeZone(source_timezone);
        var targetZone = ResolveTimeZone(target_timezone);

        // Anchor the bare time to today's date in the source zone before converting.
        var sourceLocal = DateTime.SpecifyKind(
            DateTimeOffset.UtcNow.ToUserTime(sourceZone).Date + parsed.ToTimeSpan(),
            DateTimeKind.Unspecified);
        var sourceDto = new DateTimeOffset(sourceLocal, sourceZone.GetUtcOffset(sourceLocal));
        var targetDto = TimeZoneInfo.ConvertTime(sourceDto, targetZone);

        var deltaHours = (targetDto.Offset - sourceDto.Offset).TotalHours;
        var delta = string.Create(CultureInfo.InvariantCulture, $"{(deltaHours >= 0 ? "+" : string.Empty)}{deltaHours:0.#}h");
        return string.Create(CultureInfo.InvariantCulture,
            $"{sourceDto:HH:mm} {sourceZone.Id} (UTC{sourceDto:zzz})  ->  {targetDto:HH:mm} {targetZone.Id} (UTC{targetDto:zzz})  [{delta}]");
    }

    private static TimeZoneInfo ResolveTimeZone(string timezone)
    {
        var normalized = TimeZoneResolver.NormalizeOrEmpty(timezone);
        if (string.IsNullOrEmpty(normalized))
        {
            throw new ToolFailureException(
                "The timezone identifier is invalid.",
                "Use an IANA or Windows timezone identifier.");
        }

        return TimeZoneInfo.FindSystemTimeZoneById(normalized);
    }
}
