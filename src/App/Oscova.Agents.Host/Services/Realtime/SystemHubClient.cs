using Microsoft.AspNetCore.SignalR.Client;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Host.Services.Api;
using Oscova.Agents.Host.Server.Hubs;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Host.Services.Realtime;

public sealed class SystemHubClient(
    HttpClient httpClient,
    ApiAccessTokenProvider accessTokenProvider,
    IHostApplicationLifetime applicationLifetime,
    ILogger<SystemHubClient> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private HubConnection? _connection;
    private bool _disposed;

    public Func<TaskStatusUpdate, Task>? OnTaskStatusUpdatedAsync { get; set; }
    public Func<ChannelStatus, Task>? OnChannelStatusChangedAsync { get; set; }
    public Func<NotificationMessage, Task>? OnNotificationReceivedAsync { get; set; }
    public Func<ChatThreadChangedEvent, Task>? OnThreadChangedAsync { get; set; }

    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        await _connectionGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_connection?.State is HubConnectionState.Connected or HubConnectionState.Connecting or HubConnectionState.Reconnecting)
            {
                return;
            }

            var connection = _connection ?? BuildConnection();
            _connection = connection;

            if (connection.State == HubConnectionState.Disconnected)
            {
                try
                {
                    await connection.StartAsync(ct).ConfigureAwait(false);
                }
                // A cancelled start fails every transport, so SignalR reports it as an AggregateException.
                catch (AggregateException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private HubConnection BuildConnection()
    {
        // The server-side HttpClient targets this host's own listener, which stays reachable when the browser origin is not.
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(httpClient.BaseAddress!, ApiRoutes.Hubs.System), options => options.AccessTokenProvider = accessTokenProvider.GetTokenAsync)
            .WithAutomaticReconnect()
            .WithServerTimeout(TimeSpan.FromSeconds(60))
            .Build();

        connection.HandshakeTimeout = TimeSpan.FromSeconds(30);

        connection.On<TaskStatusUpdate>(SystemHubEvents.TaskStatusUpdated, payload => PublishAsync(OnTaskStatusUpdatedAsync, payload));
        connection.On<ChannelStatus>(SystemHubEvents.ChannelStatusChanged, payload => PublishAsync(OnChannelStatusChangedAsync, payload));
        connection.On<NotificationMessage>(SystemHubEvents.NotificationReceived, payload => PublishAsync(OnNotificationReceivedAsync, payload));
        connection.On<ChatThreadChangedEvent>(SystemHubEvents.ThreadChanged, payload => PublishAsync(OnThreadChangedAsync, payload));
        connection.Reconnecting += error =>
        {
            // The server closes connections before circuit-scoped clients are disposed during host shutdown.
            if (error is not null && !IsStopping())
            {
                logger.LogWarning(error, "System hub is reconnecting.");
            }

            return Task.CompletedTask;
        };
        connection.Reconnected += _ =>
        {
            logger.LogDebug("System hub reconnected.");
            return Task.CompletedTask;
        };
        connection.Closed += error =>
        {
            if (error is not null && !IsStopping())
            {
                logger.LogWarning(error, "System hub connection closed.");
            }

            return Task.CompletedTask;
        };

        return connection;
    }

    private bool IsStopping() =>
        _disposed || applicationLifetime.ApplicationStopping.IsCancellationRequested;

    private static Task PublishAsync<TPayload>(Func<TPayload, Task>? handler, TPayload payload)
    {
        return handler is null
            ? Task.CompletedTask
            : handler(payload);
    }

    public async ValueTask DisposeAsync()
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);

        try
        {
            _disposed = true;

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }
        }
        finally
        {
            _connectionGate.Release();
            GC.SuppressFinalize(this);
        }
    }
}
