using Oscova.Agents.Channels;

namespace Oscova.Agents.Core.Notifications;

/// <summary>Reports an owned channel's running intent and last observed connection state.</summary>
public sealed record ChannelStatus(string ChannelId, bool IsRunning, ChannelConnectionState State,
    string? BotName = null, string? Error = null);
