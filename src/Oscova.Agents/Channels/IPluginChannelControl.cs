using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Channels;

/// <summary>Exposes observed state without requiring readable plugin configuration.</summary>
public sealed record PluginChannelStatus(string ChannelId, bool IsRunning, ChannelConnectionState State,
    string? BotName = null, string? Error = null);

/// <summary>Identifies an admitted agent choice without exposing host models.</summary>
public sealed record PluginChannelAgent(string Id, string Name);

/// <summary>Separates a completed persistence operation from a configuration application failure.</summary>
public sealed record PluginChannelSaveResult(PluginChannelStatus Status, string? ApplicationError = null);

/// <summary>Binds lifecycle operations to the owning plugin, channel and authenticated user.</summary>
public interface IPluginChannelControl
{
    /// <summary>Returns observed state without reading saved plugin configuration.</summary>
    Task<PluginChannelStatus> GetStatusAsync(CancellationToken ct = default);
    /// <summary>Returns available agent choices for this user.</summary>
    Task<IReadOnlyList<PluginChannelAgent>> GetAgentsAsync(CancellationToken ct = default);
    /// <summary>Rejects unavailable explicit agent choices.</summary>
    Task ValidateAgentAsync(string? agentId, CancellationToken ct = default);
    /// <summary>Starts from a fresh validated plugin snapshot.</summary>
    Task<PluginChannelStatus> StartAsync(CancellationToken ct = default);
    /// <summary>Stops the connection without loading plugin configuration.</summary>
    Task<PluginChannelStatus> StopAsync(CancellationToken ct = default);
    /// <summary>Applies independently saved configuration to an already running connection.</summary>
    Task<PluginChannelSaveResult> ApplyAsync(CancellationToken ct = default);
    /// <summary>Serializes admission, agent validation, plugin persistence and application under the channel gate.</summary>
    Task<PluginChannelSaveResult> SaveAsync(string? agentId, Func<bool, CancellationToken, Task> persist, CancellationToken ct = default);
    /// <summary>Observes complete status synchronously, callbacks must return immediately without reentering channel operations.</summary>
    Task<IDisposable> SubscribeAsync(Action<PluginChannelStatus> observer, CancellationToken ct = default);
}
