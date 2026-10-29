using Oscova.Agents.Core.Notifications;

namespace Oscova.Agents.Core.ApiContracts.Channels;

/// <summary>Confirms persistence independently from a subsequent restart failure.</summary>
public sealed record ChannelSaveResult(ChannelStatus Status, string? RestartError = null);
