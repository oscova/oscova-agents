using Oscova.Agents.Core.ApiContracts.Channels;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Channels;

namespace Oscova.Agents.Core.Channels;

/// <summary>Coordinates private per-user settings and plugin connection lifetimes.</summary>
public interface IChannelManager
{
    /// <summary>Returns saved configuration and observed status for an active channel.</summary>
    Task<ChannelConfiguration> GetAsync(string userId, string channelId, CancellationToken ct = default);
    /// <summary>Returns observed state without reading configuration.</summary>
    Task<ChannelStatus> GetStatusAsync(string userId, string channelId, CancellationToken ct = default);
    /// <summary>Serializes plugin persistence and application to an already running connection.</summary>
    Task<ChannelSaveResult> SaveAsync(string userId, string channelId, string? agentId, Func<bool, CancellationToken, Task> persist, CancellationToken ct = default);
    /// <summary>Applies saved plugin configuration without writing it.</summary>
    Task<ChannelSaveResult> ApplyAsync(string userId, string channelId, CancellationToken ct = default);
    /// <summary>Returns available current-user agent choices.</summary>
    Task<IReadOnlyList<PluginChannelAgent>> GetAgentsAsync(string userId, string channelId, CancellationToken ct = default);
    /// <summary>Validates an explicit current-user agent choice.</summary>
    Task ValidateAgentAsync(string userId, string channelId, string? agentId, CancellationToken ct = default);
    /// <summary>Starts one connection from its saved configuration.</summary>
    Task<ChannelStatus> StartAsync(string userId, string channelId, CancellationToken ct = default);
    /// <summary>Stops one connection and drains its admitted work.</summary>
    Task<ChannelStatus> StopAsync(string userId, string channelId, CancellationToken ct = default);
    /// <summary>Starts a saved startup-enabled channel.</summary>
    Task StartIfEnabledAsync(string userId, string channelId, CancellationToken ct = default);
    /// <summary>Stops all plugin connections owned by one user.</summary>
    Task StopUserAsync(string userId, CancellationToken ct = default);
    /// <summary>Sends through an existing channel connection or reports delivery failure.</summary>
    Task SendNotificationAsync(string userId, string channelId, string message, CancellationToken ct = default);
}
