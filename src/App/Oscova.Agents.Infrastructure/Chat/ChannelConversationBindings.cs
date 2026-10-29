using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Keeps notification reply destinations separate from web selection for this host's lifetime.</summary>
public sealed class ChannelConversationBindings(TimeProvider clock)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string UserId, string ChannelId), (string ThreadId, DateTimeOffset ExpiresAt)> _bindings = [];

    /// <summary>Replaces a channel's destination and starts its window when the notification is sent.</summary>
    public void Bind(string userId, string channelId, string threadId, int windowMinutes)
    {
        lock (_lock)
        {
            var key = (userId, channelId);
            var minutes = AssistantUiSettings.NormalizeChatResumeWindowMinutes(windowMinutes);
            if (minutes == 0) { _bindings.Remove(key); }
            else { _bindings[key] = (threadId, clock.GetUtcNow().AddMinutes(minutes)); }
        }
    }

    /// <summary>Returns the unexpired destination without extending its notification window.</summary>
    public string? GetThreadId(string userId, string channelId)
    {
        lock (_lock)
        {
            var key = (userId, channelId);
            if (!_bindings.TryGetValue(key, out var binding)) { return null; }

            if (clock.GetUtcNow() < binding.ExpiresAt) { return binding.ThreadId; }

            _bindings.Remove(key);
            return null;
        }
    }

    /// <summary>Ends a channel binding when the user starts a new conversation.</summary>
    public void Clear(string userId, string channelId)
    {
        lock (_lock) { _bindings.Remove((userId, channelId)); }
    }
}
