using System.Net.Http;

namespace Oscova.Agents.Channels;

/// <summary>Supplies callbacks and a connection-owned transport exempt from agent offline policy.</summary>
public sealed record ChannelConnectionContext(
    IChannelEventSink Sink,
    HttpClient Transport);
