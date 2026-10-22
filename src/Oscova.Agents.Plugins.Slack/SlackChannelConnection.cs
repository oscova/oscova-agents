using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Channels;
using SlackNet;
using SlackNet.Events;
using SlackNet.SocketMode;

namespace Oscova.Agents.Plugins.Slack;

/// <summary>Owns one Slack Socket Mode transport and translates authorized messages for host admission.</summary>
internal sealed class SlackChannelConnection : IChannelConnection
{
    private readonly AgentPluginContext _services;
    private readonly ChannelConnectionContext _connection;
    private readonly HashSet<string> _activeApps;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    private readonly string _workspaceId;
    private readonly string _authorizedUserId;
    private readonly SlackEventGuard _eventGuard;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HttpClient _files;
    private readonly TaskCompletionSource<string> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> _closureObservers = [];
    private ISlackSocketModeClient? _socket;
    private CoreSocketModeClient? _core;
    private IDisposable? _subscription;
    private SlackReplySender? _sender;
    private IWebSocket? _currentSocket;
    private Task? _connectTask;
    private string? _botUserId;
    private string? _botName;
    private string? _appId;
    private bool _ready;
    private bool _stopping;
    private bool _disposed;
    private bool _socketModeDisabled;
    private int _credentialMismatch;

    /// <summary>Creates a disconnected transport sharing this registration's app reservations.</summary>
    public SlackChannelConnection(AgentPluginContext services, ChannelConnectionContext connection, SlackSettings settings, HashSet<string> activeApps)
    {
        _services = services;
        _connection = connection;
        _files = connection.Transport;
        _activeApps = activeApps;
        _logger = services.LoggerFactory.CreateLogger<SlackChannelConnection>();
        _workspaceId = settings.WorkspaceId.Trim();
        _authorizedUserId = settings.AuthorizedUserId.Trim();
        _eventGuard = new(_workspaceId, _authorizedUserId);
    }

    /// <inheritdoc />
    [SuppressMessage("Design", "MA0054", Justification = "Vendor exceptions can carry tokens, so only a safe message leaves the plugin.")]
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var botToken = await _services.CredentialResolver.ResolveAsync(
                SlackChannel.Credential.Key, SlackChannel.BotToken, cancellationToken).ConfigureAwait(false);
            var appToken = await _services.CredentialResolver.ResolveAsync(
                SlackChannel.Credential.Key, SlackChannel.AppLevelToken, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(appToken))
            {
                throw new SlackStartException("Save both Slack bot and app-level tokens in Credentials before starting.");
            }
            _files.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
            // API calls and file reads share the connection-owned transport.
            var apiHttp = _files;
            var builder = new SlackServiceBuilder()
                .UseApiToken(botToken)
                .UseAppLevelToken(appToken)
                .UseHttp(sp => Default.Http(sp.GetJsonSettings(), () => apiHttp, sp.GetLogger()))
                .UseLogger(_ => new SafeSlackLogger(_logger, _workspaceId))
                .UseWebSocketFactory(_ => new ObservingSocketFactory(Default.WebSocketFactory, this))
                .RegisterEventHandler(new ConnectionEventHandler(this))
                .UseSocketModeClient(sp =>
                {
                    _core = new CoreSocketModeClient(sp.GetApiClient().WithAccessToken(appToken),
                        sp.GetWebSocketFactory(), sp.GetJsonSettings(), Default.Scheduler, sp.GetLogger());
                    return new SlackSocketModeClient(_core, sp.GetJsonSettings(),
                        sp.GetRequestListeners(), sp.GetHandlerFactory(), sp.GetLogger());
                });
            var api = builder.GetApiClient();
            _sender = new SlackReplySender(api, _lifetime.Token);
            _socket = builder.GetSocketModeClient();
            _subscription = _core!.Messages.Subscribe(OnSocketMessage);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            var auth = await api.Auth.Test(startup.Token).ConfigureAwait(false);
            if (!string.Equals(auth.TeamId, _workspaceId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(auth.UserId))
            {
                throw new SlackStartException("Slack bot token belongs to a different workspace or has no bot identity.");
            }
            _botUserId = auth.UserId;
            _botName = auth.User;
            _connectTask = _socket.Connect(new SocketModeConnectionOptions { NumberOfConnections = 1 }, _lifetime.Token);
            await _connectTask.WaitAsync(startup.Token).ConfigureAwait(false);
            var appId = await _hello.Task.WaitAsync(startup.Token).ConfigureAwait(false);
            startup.Token.ThrowIfCancellationRequested();
            lock (_activeApps)
            {
                if (!_activeApps.Add(appId))
                {
                    throw new SlackStartException("This Slack app is already active for another connection.");
                }
                // Only the successful claimant stores an ID that cleanup can release.
                _appId = appId;
            }
            _ready = true;
            await PublishStatusAsync(ChannelConnectionState.Connected).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            LogFailure(ex, "start");
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) { throw; }
            throw new InvalidOperationException(SafeFailure(ex));
        }
    }

    private Task HandleAsync(EventCallback callback)
    {
        lock (_gate)
        {
            if (_stopping || !_ready || _socketModeDisabled || _credentialMismatch != 0) { return Task.CompletedTask; }
            if (_eventGuard.HasMismatchedAuthorization(callback, _appId, _botUserId))
            {
                _credentialMismatch = 1;
                return PublishStatusAsync(ChannelConnectionState.Error,
                    "Slack bot and app-level tokens belong to different app installations. Save matching tokens and restart.");
            }
            if (!_eventGuard.TryAccept(callback, _appId, _botUserId, out var message)) { return Task.CompletedTask; }
            var inbound = new ChannelMessage(message.Text ?? string.Empty, message.User, message.User,
                message.Channel, message.ThreadTs, GetAttachments(message),
                (text, ct) => _sender!.SendAsync(message.Channel, message.ThreadTs, text, ct), IsBot: false);
            // Admission returns before the private agent turn, leaving Socket Mode free for approval replies.
            return _connection.Sink.ReceiveAsync(inbound, _lifetime.Token);
        }
    }

    private IReadOnlyList<ChannelAttachment> GetAttachments(MessageEvent message)
    {
        var attachments = new List<ChannelAttachment>();
        foreach (var file in message.Files ?? [])
        {
            if (file.IsExternal || string.IsNullOrWhiteSpace(file.Name) ||
                !TryGetSafeFileUri(string.IsNullOrWhiteSpace(file.UrlPrivateDownload)
                    ? file.UrlPrivate : file.UrlPrivateDownload, out var uri))
            {
                _logger.LogWarning("Rejected Slack attachment {FileId}: unsupported metadata or URL.", file.Id);
                continue;
            }
            attachments.Add(new(file.Name, file.Mimetype, file.Size, ct => OpenReadAsync(uri!, ct)));
        }
        return attachments;
    }

    [SuppressMessage("Design", "MA0054", Justification = "Vendor exceptions can carry tokens, so only a safe message leaves the plugin.")]
    private async Task<Stream> OpenReadAsync(Uri uri, CancellationToken ct)
    {
        if (!TryGetSafeFileUri(uri.AbsoluteUri, out var safeUri)) { throw new IOException("Slack attachment URL is not allowed."); }
        try
        {
            // The host invokes this only after private attachment policy and hooks allow the download.
            return await _files.GetStreamAsync(safeUri, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogFailure(ex, "download");
            throw new IOException("Slack attachment could not be downloaded. Check connectivity and retry.");
        }
    }

    private static bool TryGetSafeFileUri(string? url, out Uri? uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.IdnHost, "files.slack.com", StringComparison.OrdinalIgnoreCase) &&
            uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)) { return true; }
        uri = null;
        return false;
    }

    private void OnSocketMessage(SocketMessage message)
    {
        lock (_gate)
        {
            if (_stopping || _socketModeDisabled || _credentialMismatch != 0) { return; }
            if (message is SlackNet.SocketMode.Hello hello)
            {
                var appId = hello.ConnectionInfo?.AppId;
                if (string.IsNullOrWhiteSpace(appId)) { return; }
                if (!_ready) { _hello.TrySetResult(appId); }
                else if (string.Equals(appId, _appId, StringComparison.Ordinal))
                {
                    _ = PublishStatusAsync(ChannelConnectionState.Connected);
                }
            }
            else if (message is Disconnect disconnect)
            {
                _socketModeDisabled = disconnect.Reason == DisconnectReason.SocketModeDisabled;
                _ = PublishStatusAsync(_socketModeDisabled ? ChannelConnectionState.Error : ChannelConnectionState.Disconnected,
                    _socketModeDisabled ? "Enable Slack Socket Mode and restart the connection." : null);
            }
        }
    }

    private Task OnSocketClosedAsync(IWebSocket socket)
    {
        lock (_gate)
        {
            if (_stopping || !_ready || _credentialMismatch != 0 || _socketModeDisabled ||
                !ReferenceEquals(_currentSocket, socket)) { return Task.CompletedTask; }
            return PublishStatusAsync(ChannelConnectionState.Disconnected);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed) { return; }
        lock (_gate) { _stopping = true; }
        try
        {
            try { await _lifetime.CancelAsync().ConfigureAwait(false); }
            catch (Exception ex) { LogFailure(ex, "cancel"); }
            if (_socket is not null)
            {
                try { await _socket.DisconnectAsync().ConfigureAwait(false); }
                catch (Exception ex) { LogFailure(ex, "disconnect"); }
            }
            if (_connectTask is not null)
            {
                try { await _connectTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { LogFailure(ex, "connect cleanup"); }
            }
            Task[] observers;
            lock (_gate) { observers = [.. _closureObservers]; }
            try { await Task.WhenAll(observers).ConfigureAwait(false); }
            catch (Exception ex) { LogFailure(ex, "observer cleanup"); }
            try { _subscription?.Dispose(); }
            catch (Exception ex) { LogFailure(ex, "subscription cleanup"); }
            if (_socket is not null)
            {
                try { await _socket.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { LogFailure(ex, "socket cleanup"); }
            }
            if (_core is not null)
            {
                try { await _core.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { LogFailure(ex, "core cleanup"); }
            }
        }
        finally
        {
            _sender?.Dispose();
            _files.Dispose();
            _lifetime.Dispose();
            lock (_activeApps)
            {
                if (_appId is not null) { _activeApps.Remove(_appId); }
            }
            _disposed = true;
        }
    }

    /// <inheritdoc />
    public Task SendNotificationAsync(string text, CancellationToken cancellationToken) =>
        _sender!.SendAsync(_authorizedUserId, null, text, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    private Task PublishStatusAsync(ChannelConnectionState state, string? error = null) =>
        _connection.Sink.StatusChangedAsync(new(state, _botName, error));

    private void LogFailure(Exception ex, string operation) =>
        _logger.LogError("Slack {Operation} failed in workspace {WorkspaceId}: {ErrorType} {ErrorCode}.",
            operation, _workspaceId, ex.GetType().Name, (ex as SlackException)?.ErrorCode);

    private static string SafeFailure(Exception ex) => ex switch
    {
        SlackException { ErrorCode: "invalid_auth" or "not_authed" } =>
            "Slack bot or app-level token was rejected. Check both tokens in Credentials.",
        SlackException { ErrorCode: "missing_scope" } =>
            "Slack app is missing a required scope. Check the bot and app-level scopes, then reinstall the app.",
        OperationCanceledException => "Slack connection timed out or was cancelled. Check connectivity and try again.",
        SlackStartException => ex.Message,
        _ => "Slack connection failed. Check the workspace, tokens, scopes, and connectivity."
    };

    /// <summary>Distinguishes safe configuration errors from vendor exception messages.</summary>
    private sealed class SlackStartException(string message) : Exception(message);

    /// <summary>Routes SDK events to this connection's admission guard.</summary>
    private sealed class ConnectionEventHandler(SlackChannelConnection connection) : IEventHandler
    {
        public Task Handle(EventCallback callback) => connection.HandleAsync(callback);
    }

    /// <summary>Observes socket closure without replacing the SDK reconnect policy.</summary>
    private sealed class ObservingSocketFactory(IWebSocketFactory inner, SlackChannelConnection connection) : IWebSocketFactory
    {
        public IWebSocket Create(string uri) => new ObservingSocket(inner.Create(uri), connection);
    }

    /// <summary>Reports closure only for the current socket within the connection lifetime.</summary>
    private sealed class ObservingSocket(IWebSocket inner, SlackChannelConnection connection) : IWebSocket
    {
        public WebSocketState State => inner.State;
        public Task Closed => inner.Closed;
        public IObservable<string> Messages => inner.Messages;
        public Task Send(string message) => inner.Send(message);
        public async Task<bool> Open(CancellationToken ct)
        {
            lock (connection._gate) { connection._currentSocket = this; }
            var opened = await inner.Open(ct).ConfigureAwait(false);
            if (opened)
            {
                var observer = ObserveAsync();
                lock (connection._gate) { connection._closureObservers.Add(observer); }
            }
            return opened;
        }
        private async Task ObserveAsync()
        {
            try
            {
                await inner.Closed.WaitAsync(connection._lifetime.Token).ConfigureAwait(false);
                await connection.OnSocketClosedAsync(this).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (connection._lifetime.IsCancellationRequested) { }
        }
        public void Dispose() => inner.Dispose();
    }

    /// <summary>Logs SDK error types without request payloads or credentials.</summary>
    private sealed class SafeSlackLogger(Microsoft.Extensions.Logging.ILogger logger, string workspaceId) : SlackNet.ILogger
    {
        public void Log(ILogEvent logEvent)
        {
            if (logEvent.Category != LogCategory.Error) { return; }
            logger.LogError("Slack SDK error in workspace {WorkspaceId}: {ErrorType} {ErrorCode}.",
                workspaceId, logEvent.Exception?.GetType().Name, (logEvent.Exception as SlackException)?.ErrorCode);
        }
    }
}
