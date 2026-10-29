using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Channels;
using Oscova.Agents.Harness;
using Oscova.Agents.Core.ApiContracts.Channels;
using Oscova.Agents.Core.Channels;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.Localization;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Conversations;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Infrastructure.Secrets;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Notifications;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Serializes each user's channel settings and connection lifecycle without exposing private services to plugins.</summary>
public sealed class ChannelManager(
    ChannelCatalog catalog,
    UserServiceManager users,
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    PluginContextFactory contexts,
    PluginActivationSettings activation,
    PluginTextElicitationTransport replies,
    TextChannelTurnProcessor turnProcessor,
    IChatAttachmentService attachments,
    NotificationHub<ChannelStatus> statusHub, IServerTextLocalizer text) : IChannelManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<(string UserId, string ChannelId), Binding> _bindings = new();
    private readonly ILogger _logger = loggerFactory.CreateLogger<ChannelManager>();
    private bool _disposed;

    private readonly Lock _ownershipGate = new();
    private readonly HashSet<string> _retiredUsers = new(StringComparer.Ordinal);

    private void RequireAccess(string userId, string channelId, Binding? binding = null)
    {
        lock (_ownershipGate)
        {
            ObjectDisposedException.ThrowIf(_retiredUsers.Contains(userId) || binding?.Retired == true, this);
        }

        var plugin = catalog.Get(channelId).Plugin;
        // A plugin saved disabled stays loaded until restart, so its channel is unavailable like an absent one.
        if (!activation.IsEnabled(plugin.Manifest.Id)) { throw new KeyNotFoundException("The channel is unavailable. Enable its plugin and restart."); }

        contexts.RequireUserAccess(plugin, userId);
    }

    private async Task<Binding> GetBindingAsync(string userId, string channelId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var definition = catalog.Get(channelId).Contribution.Definition;
        RequireAccess(userId, channelId);
        await users.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        lock (_ownershipGate)
        {
            ObjectDisposedException.ThrowIf(_retiredUsers.Contains(userId), this);
            return _bindings.GetOrAdd((userId, definition.Id), _ => new Binding(definition.Id));
        }
    }

    private async Task<ChannelRuntimeConfiguration> ReadConfigurationAsync(string userId, string channelId, bool complete, CancellationToken ct)
    {
        RequireAccess(userId, channelId);
        var scope = await users.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var (plugin, contribution) = catalog.Get(channelId);
        var context = contexts.Create(plugin,
            new ToolCredentialResolverAdapter(scope.CredentialService, plugin.DeclaredCredentialKeys, plugin.Manifest.Id),
            new PluginContextFactory.UserTimeProvider(scope.UiSettings.GetTimeZone), scope.OAuthTokenService, userId);
        // The plugin alone reads its saved model and captures the connection's configuration.
        return await contribution.Channel.ReadConfigurationAsync(context, complete, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ChannelStatus> GetStatusAsync(string userId, string channelId, CancellationToken ct = default)
    {
        var binding = await GetBindingAsync(userId, channelId, ct).ConfigureAwait(false);
        RequireAccess(userId, channelId, binding);
        return binding.Status;
    }

    /// <inheritdoc />
    public async Task<ChannelConfiguration> GetAsync(string userId, string channelId, CancellationToken ct = default)
    {
        var binding = await GetBindingAsync(userId, channelId, ct).ConfigureAwait(false);
        await binding.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireAccess(userId, channelId, binding);
            var configuration = await ReadConfigurationAsync(userId, channelId, false, ct).ConfigureAwait(false);
            return new(catalog.Get(channelId).Contribution.Definition, configuration.StartAtStartup,
                configuration.AgentId, binding.Status);
        }
        finally { binding.Gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PluginChannelAgent>> GetAgentsAsync(string userId, string channelId, CancellationToken ct = default)
    {
        RequireAccess(userId, channelId);
        var scope = await users.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        await scope.AppService.ReloadAgentDefinitionsAsync().ConfigureAwait(false);
        return scope.AppService.CustomAgentDefinitions.Prepend(scope.AppService.MainAgentDefinition)
            .Select(agent => new PluginChannelAgent(agent.Id, agent.Name)).ToArray();
    }

    /// <inheritdoc />
    public async Task ValidateAgentAsync(string userId, string channelId, string? agentId, CancellationToken ct = default)
    {
        var agents = await GetAgentsAsync(userId, channelId, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(agentId) && !agents.Any(agent => agent.Id == agentId))
        { throw new ArgumentException("Select an available agent.", nameof(agentId)); }
    }

    /// <inheritdoc />
    public async Task<ChannelSaveResult> SaveAsync(string userId, string channelId, string? agentId,
        Func<bool, CancellationToken, Task> persist, CancellationToken ct = default)
    {
        var binding = await GetBindingAsync(userId, channelId, ct).ConfigureAwait(false);
        await binding.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireAccess(userId, channelId, binding);
            await ValidateAgentAsync(userId, channelId, agentId, ct).ConfigureAwait(false);
            // Persistence failures leave the active connection untouched. Callbacks cannot reenter this gate.
            await persist(binding.Connection is not null, ct).ConfigureAwait(false);
            return await ApplyRunningAsync(userId, channelId, binding).ConfigureAwait(false);
        }
        finally { binding.Gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<ChannelSaveResult> ApplyAsync(string userId, string channelId, CancellationToken ct = default)
    {
        var binding = await GetBindingAsync(userId, channelId, ct).ConfigureAwait(false);
        await binding.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireAccess(userId, channelId, binding);
            return await ApplyRunningAsync(userId, channelId, binding).ConfigureAwait(false);
        }
        finally { binding.Gate.Release(); }
    }

    private async Task<ChannelSaveResult> ApplyRunningAsync(string userId, string channelId, Binding binding)
    {
        if (binding.Connection is null) { return new(binding.Status); }

        var scope = await users.GetOrCreateScopeAsync(userId).ConfigureAwait(false);
        ChannelRuntimeConfiguration configuration;
        try { configuration = await ReadConfigurationAsync(userId, channelId, true, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saved channel {ChannelId} configuration could not be read for user {UserId}.", channelId, userId);
            return new(binding.Status, text.Localize(scope.UiSettings.CultureName, NotificationTextKeys.ChannelSavedNotApplied,
                "Settings were saved, but could not be applied. The existing connection is still running. Check the settings and try again."));
        }

        try
        {
            await StopConnectionAsync(userId, binding).ConfigureAwait(false);
            await StartConnectionAsync(userId, channelId, binding, CancellationToken.None, configuration).ConfigureAwait(false);
            return new(binding.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saved channel {ChannelId} could not restart for user {UserId}.", channelId, userId);
            return new(binding.Status, "Settings were saved, but the channel could not restart. Check credentials and connectivity, then start again.");
        }
    }

    /// <inheritdoc />
    public async Task<ChannelStatus> StartAsync(string userId, string channelId, CancellationToken ct = default)
    {
        var binding = await GetBindingAsync(userId, channelId, ct).ConfigureAwait(false);
        await binding.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireAccess(userId, channelId, binding);
            await StartConnectionAsync(userId, channelId, binding, ct).ConfigureAwait(false);
            return binding.Status;
        }
        finally { binding.Gate.Release(); }
    }

    /// <inheritdoc />
    public async Task StartIfEnabledAsync(string userId, string channelId, CancellationToken ct = default)
    {
        var binding = await GetBindingAsync(userId, channelId, ct).ConfigureAwait(false);
        await binding.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireAccess(userId, channelId, binding);
            var configuration = await ReadConfigurationAsync(userId, channelId, false, ct).ConfigureAwait(false);
            if (configuration.StartAtStartup)
            { await StartConnectionAsync(userId, channelId, binding, ct, configuration).ConfigureAwait(false); }
        }
        finally { binding.Gate.Release(); }
    }

    private async Task StartConnectionAsync(string userId, string channelId, Binding binding, CancellationToken ct,
        ChannelRuntimeConfiguration? configuration = null)
    {
        RequireAccess(userId, channelId, binding);
        if (binding.Connection is not null) { return; }

        configuration ??= await ReadConfigurationAsync(userId, channelId, true, ct).ConfigureAwait(false);
        var scope = await users.GetOrCreateScopeAsync(userId, ct).ConfigureAwait(false);
        var (plugin, contribution) = catalog.Get(channelId);
        var handler = new PluginChannelMessageHandler(plugin.Manifest.Id, contribution.Definition, configuration.AgentId,
            scope, replies, turnProcessor, attachments, loggerFactory.CreateLogger<PluginChannelMessageHandler>());
        binding.Handler = handler;
        try
        {
            var transport = httpClientFactory.CreateClient(PluginHostServices.ChannelTransport);
            IChannelConnection connection;
            try { connection = configuration.CreateConnection(new(new ChannelEventSink(this, userId, binding, handler), transport)); }
            catch { transport.Dispose(); throw; }

            binding.Connection = connection;
            await PublishAsync(userId, binding, new(channelId, true, ChannelConnectionState.Starting)).ConfigureAwait(false);
            await connection.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { await StopConnectionAsync(userId, binding).ConfigureAwait(false); }
            catch (Exception cleanupError) { _logger.LogError(cleanupError, "Failed to clean up channel {ChannelId}.", channelId); }

            _logger.LogError(ex, "Channel {ChannelId} failed to start for user {UserId}.", channelId, userId);
            await PublishAsync(userId, binding, new(channelId, binding.Connection is not null, ChannelConnectionState.Error,
                Error: "Channel could not start. Check credentials, configuration, and connectivity.")).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ChannelStatus> StopAsync(string userId, string channelId, CancellationToken ct = default)
    {
        var binding = await GetBindingAsync(userId, channelId, ct).ConfigureAwait(false);
        await binding.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireAccess(userId, channelId, binding);
            await StopConnectionAsync(userId, binding).ConfigureAwait(false);
            return binding.Status;
        }
        finally { binding.Gate.Release(); }
    }

    private async Task StopConnectionAsync(string userId, Binding binding)
    {
        var connection = binding.Connection;
        PluginChannelMessageHandler? handler;
        await binding.StatusGate.WaitAsync().ConfigureAwait(false);
        try
        {
            handler = binding.Handler;
            binding.Handler = null;
        }
        finally { binding.StatusGate.Release(); }

        try
        {
            try
            {
                if (handler is not null) { await handler.DisposeAsync().ConfigureAwait(false); }
            }
            finally
            {
                if (connection is not null)
                {
                    try { await connection.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                    finally
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                        binding.Connection = null;
                    }
                }
            }

            await PublishAsync(userId, binding, new(binding.Status.ChannelId, false, ChannelConnectionState.Stopped)).ConfigureAwait(false);
        }
        catch
        {
            await PublishAsync(userId, binding, new(binding.Status.ChannelId, binding.Connection is not null,
                ChannelConnectionState.Error, Error: "Channel shutdown failed. Check the connection logs.")).ConfigureAwait(false);
            throw;
        }
    }

    private Task PublishCallbackAsync(string userId, Binding binding, PluginChannelMessageHandler handler, ChannelConnectionStatus status) =>
        PublishAsync(userId, binding, new(binding.Status.ChannelId, true, status.State, status.BotName, status.Error), handler);

    private async Task PublishAsync(string userId, Binding binding, ChannelStatus status, PluginChannelMessageHandler? expected = null)
    {
        await binding.StatusGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (expected is not null && !ReferenceEquals(binding.Handler, expected)) { return; }

            binding.Status = status;
            await statusHub.NotifyAsync(userId, status).ConfigureAwait(false);
        }
        finally { binding.StatusGate.Release(); }
    }

    /// <inheritdoc />
    public async Task StopUserAsync(string userId, CancellationToken ct = default)
    {
        if (_disposed) { return; }

        Binding[] bindings;
        lock (_ownershipGate)
        {
            _retiredUsers.Add(userId);
            bindings = _bindings.Where(pair => pair.Key.UserId == userId).Select(pair => pair.Value).ToArray();
        }

        foreach (var binding in bindings)
        {
            await binding.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                binding.Retired = true;
                await StopConnectionAsync(userId, binding).ConfigureAwait(false);
            }
            finally { binding.Gate.Release(); }
        }
    }
    /// <inheritdoc />
    public async Task SendNotificationAsync(string userId, string channelId, string message, CancellationToken ct = default)
    {
        var binding = await GetBindingAsync(userId, channelId, ct).ConfigureAwait(false);
        await binding.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireAccess(userId, channelId, binding);
            var connection = binding.Connection ?? throw new ToolFailureException("The channel is not running. Start it in channel settings before sending notifications.");
            await connection.SendNotificationAsync(message, ct).ConfigureAwait(false);
        }
        finally { binding.Gate.Release(); }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }

        foreach (var userId in _bindings.Keys.Select(key => key.UserId).Distinct(StringComparer.Ordinal))
        {
            await StopUserAsync(userId).ConfigureAwait(false);
        }

        _disposed = true;
    }

    private sealed class Binding(string channelId)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public SemaphoreSlim StatusGate { get; } = new(1, 1);
        public ChannelStatus Status { get; set; } = new(channelId, false, ChannelConnectionState.Stopped);
        public IChannelConnection? Connection { get; set; }
        public PluginChannelMessageHandler? Handler { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class ChannelEventSink(ChannelManager owner, string userId, Binding binding,
        PluginChannelMessageHandler handler) : IChannelEventSink
    {
        public Task ReceiveAsync(ChannelMessage message, CancellationToken cancellationToken)
            => handler.ReceiveAsync(message, cancellationToken);
        public Task StatusChangedAsync(ChannelConnectionStatus status)
            => owner.PublishCallbackAsync(userId, binding, handler, status);
    }
}
