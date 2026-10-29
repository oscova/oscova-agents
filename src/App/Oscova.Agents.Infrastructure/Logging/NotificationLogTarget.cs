using NLog;
using NLog.Targets;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Notifications;

namespace Oscova.Agents.Infrastructure.Logging;

/// <summary>
/// Mirrors warning and error log events into the in-app notification hub.
/// </summary>
[Target("NotificationInbox")]
public sealed class NotificationLogTarget : TargetWithLayout
{
    protected override void Write(LogEventInfo logEvent)
    {
        try
        {
            if (IsNotificationHandled(logEvent))
            {
                return;
            }

            var hub = ResolveService<NotificationHub<NotificationMessage>>();
            if (hub is null)
            {
                return;
            }

            var message = RenderLogMessage(logEvent);
            var title = BuildTitle(logEvent, message);
            var kind = logEvent.Level >= LogLevel.Error
                ? NotificationKind.SystemError
                : NotificationKind.SystemWarning;
            var userId = TryGetUserId(logEvent);
            var payload = new NotificationMessage(
                title,
                message,
                kind,
                UserId: userId);

            _ = string.IsNullOrWhiteSpace(userId)
                ? hub.NotifyAllAsync(payload)
                : hub.NotifyAsync(userId, payload);
        }
        catch
        {
            // Logging targets must not affect the log event they are observing.
        }
    }

    private static bool IsNotificationHandled(LogEventInfo logEvent)
    {
        if (logEvent.Properties.TryGetValue("notificationHandled", out var eventValue) && IsTrue(eventValue))
        {
            return true;
        }

        foreach (var property in ScopeContext.GetAllProperties())
        {
            if (string.Equals(property.Key?.ToString(), "notificationHandled", StringComparison.OrdinalIgnoreCase) && IsTrue(property.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTrue(object? value)
    {
        return value is true ||
            (value is string text && bool.TryParse(text, out var parsed) && parsed);
    }

    private string RenderLogMessage(LogEventInfo logEvent)
    {
        var rendered = Layout?.Render(logEvent);
        if (string.IsNullOrWhiteSpace(rendered))
        {
            rendered = logEvent.FormattedMessage;
        }

        if (logEvent.Exception is not null && string.IsNullOrWhiteSpace(rendered))
        {
            rendered = logEvent.Exception.Message;
        }

        rendered = string.IsNullOrWhiteSpace(rendered) ? logEvent.LoggerName : rendered.Trim();
        return rendered.Length <= 280 ? rendered : rendered[..277] + "...";
    }

    private static string BuildTitle(LogEventInfo logEvent, string message)
    {
        if (logEvent.Exception is not null)
        {
            return logEvent.Exception.GetType().Name;
        }

        var sentenceEnd = message.IndexOf('.', StringComparison.Ordinal);
        var title = sentenceEnd > 0 ? message[..sentenceEnd] : message;
        return title.Length <= 80 ? title : title[..77] + "...";
    }

    private static string? TryGetUserId(LogEventInfo logEvent)
    {
        if (logEvent.Properties.TryGetValue("userId", out var propertyUserId) && propertyUserId is not null)
        {
            return propertyUserId.ToString();
        }

        foreach (var property in ScopeContext.GetAllProperties())
        {
            if (string.Equals(property.Key?.ToString(), "userId", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value?.ToString();
            }
        }

        return null;
    }
}
