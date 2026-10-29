using Oscova.Agents.Channels;
using Oscova.Agents.Core.Channels;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Notifications;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Adapts private lifecycle and notifications to one admitted plugin channel and user.</summary>
public sealed class BoundPluginChannelControl(IChannelManager manager, NotificationHub<ChannelStatus> notifications,
    string userId, string channelId, Func<CancellationToken, Task> require) : IPluginChannelControl
{
    private static PluginChannelStatus Map(ChannelStatus status) =>
        new(status.ChannelId, status.IsRunning, status.State, status.BotName, status.Error);

    /// <inheritdoc />
    public async Task<PluginChannelStatus> GetStatusAsync(CancellationToken ct = default)
    { await require(ct).ConfigureAwait(false); return Map(await manager.GetStatusAsync(userId, channelId, ct).ConfigureAwait(false)); }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PluginChannelAgent>> GetAgentsAsync(CancellationToken ct = default)
    { await require(ct).ConfigureAwait(false); return await manager.GetAgentsAsync(userId, channelId, ct).ConfigureAwait(false); }

    /// <inheritdoc />
    public async Task ValidateAgentAsync(string? agentId, CancellationToken ct = default)
    { await require(ct).ConfigureAwait(false); await manager.ValidateAgentAsync(userId, channelId, agentId, ct).ConfigureAwait(false); }

    /// <inheritdoc />
    public async Task<PluginChannelStatus> StartAsync(CancellationToken ct = default)
    { await require(ct).ConfigureAwait(false); return Map(await manager.StartAsync(userId, channelId, ct).ConfigureAwait(false)); }

    /// <inheritdoc />
    public async Task<PluginChannelStatus> StopAsync(CancellationToken ct = default)
    { await require(ct).ConfigureAwait(false); return Map(await manager.StopAsync(userId, channelId, ct).ConfigureAwait(false)); }

    /// <inheritdoc />
    public async Task<PluginChannelSaveResult> ApplyAsync(CancellationToken ct = default)
    {
        await require(ct).ConfigureAwait(false);
        var result = await manager.ApplyAsync(userId, channelId, ct).ConfigureAwait(false);
        return new(Map(result.Status), result.RestartError);
    }

    /// <inheritdoc />
    public async Task<PluginChannelSaveResult> SaveAsync(string? agentId, Func<bool, CancellationToken, Task> persist, CancellationToken ct = default)
    {
        await require(ct).ConfigureAwait(false);
        var result = await manager.SaveAsync(userId, channelId, agentId, async (complete, token) =>
        {
            // Recheck panel authority inside the manager's gate before plugin persistence.
            await require(token).ConfigureAwait(false);
            await persist(complete, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return new(Map(result.Status), result.RestartError);
    }

    /// <inheritdoc />
    public async Task<IDisposable> SubscribeAsync(Action<PluginChannelStatus> observer, CancellationToken ct = default)
    {
        await require(ct).ConfigureAwait(false);
        return notifications.Subscribe(userId, async status =>
        {
            if (!string.Equals(status.ChannelId, channelId, StringComparison.OrdinalIgnoreCase)) { return; }

            await require(CancellationToken.None).ConfigureAwait(false);
            observer(Map(status));
        });
    }
}
