using SlackNet.Events;

namespace Oscova.Agents.Plugins.Slack;

/// <summary>Accepts only one owner's direct Slack messages once per event ID.</summary>
internal sealed class SlackEventGuard(string workspaceId, string slackUserId)
{
    private readonly Dictionary<string, DateTimeOffset> _events = new(StringComparer.Ordinal);
    private readonly Queue<string> _eventOrder = new();
    private readonly Lock _gate = new();

    /// <summary>Detects matching workspace and app events authorized for another bot installation.</summary>
    public bool HasMismatchedAuthorization(EventCallback callback, string? appId, string? botUserId) =>
        !string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(botUserId) &&
        string.Equals(callback.TeamId, workspaceId, StringComparison.Ordinal) &&
        string.Equals(callback.ApiAppId, appId, StringComparison.Ordinal) &&
        callback.Authorizations?.Any(auth => auth.IsBot &&
            string.Equals(auth.TeamId, workspaceId, StringComparison.Ordinal)) == true &&
        !HasBotAuthorization(callback, botUserId);

    /// <summary>Accepts an authorized direct message once within the bounded delivery window.</summary>
    public bool TryAccept(EventCallback callback, string? appId, string? botUserId, out MessageEvent message)
    {
        message = null!;
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(botUserId) ||
            !string.Equals(callback.TeamId, workspaceId, StringComparison.Ordinal) ||
            !string.Equals(callback.ApiAppId, appId, StringComparison.Ordinal) ||
            callback.IsExtSharedChannel || string.IsNullOrWhiteSpace(callback.EventId) ||
            !HasBotAuthorization(callback, botUserId) ||
            callback.Event is not MessageEvent candidate ||
            !string.Equals(candidate.ChannelType, "im", StringComparison.Ordinal) ||
            candidate.Hidden ||
            candidate.Subtype is not (null or "" or "file_share") ||
            !string.Equals(candidate.User, slackUserId, StringComparison.Ordinal) ||
            string.Equals(candidate.User, botUserId, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(candidate.BotId) || !string.IsNullOrEmpty(candidate.AppId) ||
            candidate.BotProfile is not null || candidate.DisplayAsBot ||
            string.IsNullOrWhiteSpace(candidate.Channel) || string.IsNullOrWhiteSpace(candidate.Ts))
        {
            return false;
        }

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            while (_eventOrder.Count > 0 &&
                (!_events.TryGetValue(_eventOrder.Peek(), out var seen) ||
                 seen < now - TimeSpan.FromMinutes(10)))
            {
                _events.Remove(_eventOrder.Dequeue());
            }
            if (_events.ContainsKey(callback.EventId)) { return false; }
            if (_events.Count >= 1024) { _events.Remove(_eventOrder.Dequeue()); }
            _events[callback.EventId] = now;
            _eventOrder.Enqueue(callback.EventId);
        }
        message = candidate;
        return true;
    }

    private bool HasBotAuthorization(EventCallback callback, string? botUserId) =>
        callback.Authorizations?.Any(auth => auth.IsBot &&
            string.Equals(auth.TeamId, workspaceId, StringComparison.Ordinal) &&
            string.Equals(auth.UserId, botUserId, StringComparison.Ordinal)) == true;
}
