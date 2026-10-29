using Oscova.Agents.Channels;
using Oscova.Agents.Core.Notifications;

namespace Oscova.Agents.Core.ApiContracts.Channels;

/// <summary>Returns one user's saved channel configuration and active declaration without credentials.</summary>
public sealed record ChannelConfiguration(ChannelDefinition Definition, bool StartAtStartup, string? AgentId,
    ChannelStatus Status);
