using System;

namespace Oscova.Agents.Channels;

/// <summary>Exposes operational facts and a factory capturing the plugin's private configuration snapshot.</summary>
public sealed record ChannelRuntimeConfiguration(bool StartAtStartup, string? AgentId,
    Func<ChannelConnectionContext, IChannelConnection> CreateConnection);
