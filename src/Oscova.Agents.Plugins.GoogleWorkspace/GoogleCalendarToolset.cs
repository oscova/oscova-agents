using System.ComponentModel;
using System.Globalization;
using System.Text;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Plugins.GoogleWorkspace;

/// <summary>
/// Exposes read-only Google Calendar tools backed by the shared OAuth token service.
/// </summary>
public sealed class GoogleCalendarToolset : IAgentToolset
{
    private const string NotConnectedMessage = "Google Calendar is not connected. Ask the user to connect their Google account.";

    private readonly AgentPluginContext _context;
    private readonly ILogger<GoogleCalendarToolset> _logger;

    /// <summary>Creates GoogleCalendar tools with caller-owned account access and time zone.</summary>
    public GoogleCalendarToolset(AgentPluginContext context)
    {
        _context = context;
        _logger = context.LoggerFactory.CreateLogger<GoogleCalendarToolset>();
    }

    /// <summary>Lists events in the connected Google calendar.</summary>
    [DisplayName("list")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [AgentTool]
    [Description("""
        Lists upcoming Google Calendar events within a time range.
        Returns event title, start/end time, location, attendees, and a Google Meet link when present.
        All-day events show a date only (no time).
        Use get with a returned event ID to inspect the full description and details.
        """)]
    public async Task<string> ListEventsAsync(
        [Description("Start of the time range in ISO 8601 format (e.g. '2026-04-03T00:00:00Z'). Defaults to now if not provided.")] string? timeMin = null,
        [Description("End of the time range in ISO 8601 format (e.g. '2026-04-10T23:59:59Z'). Defaults to 24 hours from now if not provided.")] string? timeMax = null,
        [Description("Maximum number of events to return (default 10, max 50).")] int maxResults = 10,
        CancellationToken ct = default)
    {
        return await GoogleWorkspaceExecution.ExecuteAsync(async () =>
        {
            var service = await CreateServiceAsync(ct).ConfigureAwait(false);
            if (service is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            maxResults = Math.Clamp(maxResults, 1, 50);

            var now = DateTimeOffset.UtcNow;
            var resolvedTimeMin = string.IsNullOrWhiteSpace(timeMin) ? now : ParseIso8601(timeMin, _context.UserTimeZone);
            var resolvedTimeMax = string.IsNullOrWhiteSpace(timeMax) ? resolvedTimeMin.AddDays(1) : ParseIso8601(timeMax, _context.UserTimeZone);

            if (resolvedTimeMax < resolvedTimeMin)
            {
                throw new ToolFailureException("The calendar time range is invalid.", "Use a timeMax at or after timeMin.");
            }

            var request = service.Events.List("primary");
            request.TimeMinDateTimeOffset = resolvedTimeMin;
            request.TimeMaxDateTimeOffset = resolvedTimeMax;
            request.MaxResults = maxResults;
            request.SingleEvents = true;
            request.ShowDeleted = false;
            request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

            var result = await request.ExecuteAsync(ct).ConfigureAwait(false);
            var events = result.Items?
                .Where(calendarEvent => !string.Equals(calendarEvent.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (events is null || events.Count == 0)
            {
                return "No events found in the specified time range.";
            }

            var userTimeZone = _context.UserTimeZone;
            var builder = new StringBuilder();
            builder.AppendLine($"Showing {events.Count} event(s):")
                .AppendLine(CultureInfo.InvariantCulture,
                $"Range: {TimeZoneInfo.ConvertTime(resolvedTimeMin, userTimeZone):yyyy-MM-dd HH:mm} -> {TimeZoneInfo.ConvertTime(resolvedTimeMax, userTimeZone):yyyy-MM-dd HH:mm}")
                .AppendLine();

            for (var index = 0; index < events.Count; index++)
            {
                var calendarEvent = events[index];
                builder.AppendLine($"[{index + 1}] ID: {calendarEvent.Id}")
                    .AppendLine($"    Title: {calendarEvent.Summary ?? "(no title)"}")
                    .AppendLine($"    When: {FormatEventTime(calendarEvent, userTimeZone)}");

                if (!string.IsNullOrWhiteSpace(calendarEvent.Location))
                {
                    builder.AppendLine($"    Location: {calendarEvent.Location}");
                }

                if (!string.IsNullOrWhiteSpace(calendarEvent.HangoutLink))
                {
                    builder.AppendLine($"    Meet: {calendarEvent.HangoutLink}");
                }

                var attendees = FormatAttendees(calendarEvent.Attendees);
                if (attendees.Count > 0)
                {
                    builder.AppendLine($"    Attendees: {string.Join(", ", attendees)}");
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }, _logger, "Google Calendar", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>Reads a Google Calendar event by ID.</summary>
    [DisplayName("get")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [AgentTool]
    [Description("Gets the full details of a specific Google Calendar event by its ID. Use list first to get event IDs.")]
    public async Task<string> GetEventAsync(
        [Description("The Google Calendar event ID obtained from list.")] string eventId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ToolFailureException("A calendar event ID is required.", "Use an ID returned by list.");
        }

        return await GoogleWorkspaceExecution.ExecuteAsync(async () =>
        {
            var service = await CreateServiceAsync(ct).ConfigureAwait(false);
            if (service is null)
            {
                throw new ToolFailureException(NotConnectedMessage);
            }

            var calendarEvent = await service.Events.Get("primary", eventId).ExecuteAsync(ct).ConfigureAwait(false);
            if (string.Equals(calendarEvent.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return "That event is cancelled.";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Title: {calendarEvent.Summary ?? "(no title)"}")
                .AppendLine($"When: {FormatEventTime(calendarEvent, _context.UserTimeZone)}");

            if (!string.IsNullOrWhiteSpace(calendarEvent.Location))
            {
                builder.AppendLine($"Location: {calendarEvent.Location}");
            }

            if (!string.IsNullOrWhiteSpace(calendarEvent.HangoutLink))
            {
                builder.AppendLine($"Meet: {calendarEvent.HangoutLink}");
            }

            if (!string.IsNullOrWhiteSpace(calendarEvent.HtmlLink))
            {
                builder.AppendLine($"Calendar link: {calendarEvent.HtmlLink}");
            }

            var organizer = FormatEmailDisplay(calendarEvent.Organizer?.DisplayName, calendarEvent.Organizer?.Email);
            if (!string.IsNullOrWhiteSpace(organizer))
            {
                builder.AppendLine($"Organizer: {organizer}");
            }

            var attendees = FormatAttendeesWithStatus(calendarEvent.Attendees);
            if (attendees.Count > 0)
            {
                builder.AppendLine("Attendees:");
                foreach (var attendee in attendees)
                {
                    builder.AppendLine($"  {attendee}");
                }
            }

            if (!string.IsNullOrWhiteSpace(calendarEvent.Description))
            {
                builder.AppendLine()
                    .AppendLine("Description:")
                    .AppendLine(calendarEvent.Description);
            }

            return builder.ToString().TrimEnd();
        }, _logger, "Google Calendar", NotConnectedMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a Calendar API client for the current user if a valid Google access token is available.
    /// </summary>
    private async Task<CalendarService?> CreateServiceAsync(CancellationToken ct)
    {
        var initializer = await GoogleWorkspaceClientFactory.CreateInitializerAsync(_context, ct).ConfigureAwait(false);
        return initializer is null ? null : new CalendarService(initializer);
    }

    /// <summary>
    /// Formats Google Calendar event timing for both timed and all-day events.
    /// </summary>
    private static string FormatEventTime(Event calendarEvent, TimeZoneInfo userTimeZone)
    {
        if (calendarEvent.Start is null)
        {
            return "(no time)";
        }

        if (!string.IsNullOrWhiteSpace(calendarEvent.Start.Date))
        {
            var startDate = calendarEvent.Start.Date;
            var endDate = calendarEvent.End?.Date;

            if (string.IsNullOrWhiteSpace(endDate) || startDate == endDate)
            {
                return $"{startDate} (all day)";
            }

            var parsedStartDate = DateOnly.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDay);
            var parsedEndDate = DateOnly.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDay);
            if (parsedStartDate && parsedEndDate)
            {
                // Google stores all-day end dates as exclusive, so subtract one day for display.
                var inclusiveEndDay = endDay.AddDays(-1);
                if (inclusiveEndDay == startDay)
                {
                    return $"{startDate} (all day)";
                }

                return $"{startDay:yyyy-MM-dd} -> {inclusiveEndDay:yyyy-MM-dd} (all day)";
            }

            return $"{startDate} -> {endDate} (all day)";
        }

        if (calendarEvent.Start.DateTimeDateTimeOffset is not { } startDateTime)
        {
            return "(unknown time)";
        }

        var startUserTime = TimeZoneInfo.ConvertTime(startDateTime, userTimeZone);
        if (calendarEvent.End?.DateTimeDateTimeOffset is not { } endDateTime)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{startUserTime:yyyy-MM-dd HH:mm}");
        }

        var endUserTime = TimeZoneInfo.ConvertTime(endDateTime, userTimeZone);
        if (startUserTime.Date == endUserTime.Date)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{startUserTime:yyyy-MM-dd HH:mm} -> {endUserTime:HH:mm}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{startUserTime:yyyy-MM-dd HH:mm} -> {endUserTime:yyyy-MM-dd HH:mm}");
    }

    /// <summary>
    /// Parses a time range bound, reading a value without an offset as the user's wall-clock time.
    /// </summary>
    private static DateTimeOffset ParseIso8601(string value, TimeZoneInfo userTimeZone)
    {
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            throw new ToolFailureException("The calendar time range is invalid.", "Use ISO 8601 dates and times.");
        }

        return parsed.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(parsed, userTimeZone.GetUtcOffset(parsed))
            : new DateTimeOffset(parsed.ToUniversalTime());
    }

    /// <summary>
    /// Formats attendees for compact event list output.
    /// </summary>
    private static List<string> FormatAttendees(IList<EventAttendee>? attendees)
    {
        if (attendees is null || attendees.Count == 0)
        {
            return [];
        }

        return [.. attendees
            .Select(attendee => FormatEmailDisplay(attendee.DisplayName, attendee.Email))
            .Where(attendee => !string.IsNullOrWhiteSpace(attendee))
            .Cast<string>()];
    }

    /// <summary>
    /// Formats attendees with their RSVP status for the detailed event view.
    /// </summary>
    private static List<string> FormatAttendeesWithStatus(IList<EventAttendee>? attendees)
    {
        if (attendees is null || attendees.Count == 0)
        {
            return [];
        }

        var formattedAttendees = new List<string>();
        foreach (var attendee in attendees)
        {
            var name = FormatEmailDisplay(attendee.DisplayName, attendee.Email);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var status = attendee.ResponseStatus switch
            {
                "accepted" => "[accepted]",
                "declined" => "[declined]",
                "tentative" => "[tentative]",
                "needsAction" => "[needs action]",
                _ => "[unknown]"
            };

            formattedAttendees.Add($"{status} {name}");
        }

        return formattedAttendees;
    }

    /// <summary>
    /// Combines display name and email into a readable contact string.
    /// </summary>
    private static string? FormatEmailDisplay(string? displayName, string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(displayName)
            ? email
            : $"{displayName} <{email}>";
    }
}
