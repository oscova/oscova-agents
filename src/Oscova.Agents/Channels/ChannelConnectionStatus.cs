namespace Oscova.Agents.Channels;

/// <summary>Reports vendor connection state independently from the host's running intent.</summary>
public sealed record ChannelConnectionStatus(ChannelConnectionState State, string? BotName = null, string? Error = null);

/// <summary>Identifies the last observed connection state.</summary>
public enum ChannelConnectionState
{
    /// <summary>The connection is stopped.</summary>
    Stopped,
    /// <summary>The connection is starting.</summary>
    Starting,
    /// <summary>The vendor has confirmed readiness.</summary>
    Connected,
    /// <summary>The connection is temporarily disconnected.</summary>
    Disconnected,
    /// <summary>The connection reported a failure.</summary>
    Error
}
