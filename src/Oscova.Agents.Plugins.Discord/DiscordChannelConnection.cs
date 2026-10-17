using System.Globalization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Channels;

namespace Oscova.Agents.Plugins.Discord;

/// <summary>Translates Discord events into normalized messages for the host.</summary>
public sealed class DiscordChannelConnection : IChannelConnection
{
    private const int MessageLimit = 2000;
    private readonly AgentPluginContext _services;
    private readonly ChannelConnectionContext _connection;
    private readonly ILogger _logger;
    private readonly DiscordSocketClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ulong _authorizedUserId;

    /// <summary>Creates a disconnected client using one immutable configuration snapshot.</summary>
    public DiscordChannelConnection(AgentPluginContext services, ChannelConnectionContext connection, DiscordSettings settings)
    {
        _services = services;
        _connection = connection;
        _logger = services.LoggerFactory.CreateLogger<DiscordChannelConnection>();
        _authorizedUserId = ulong.Parse(settings.AuthorizedUserId, CultureInfo.InvariantCulture);
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            UseInteractionSnowflakeDate = false,
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages |
                GatewayIntents.DirectMessages | GatewayIntents.MessageContent
        });
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.Ready += OnReadyAsync;
        _client.Disconnected += OnDisconnectedAsync;
        _client.Log += OnLogAsync;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var token = await _services.CredentialResolver.ResolveAsync(
            DiscordChannel.Credential.Key, DiscordChannel.BotToken, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token)) { throw new InvalidOperationException("Discord bot token is not configured."); }
        cancellationToken.ThrowIfCancellationRequested();
        await _client.LoginAsync(TokenType.Bot, token).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _client.StartAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _client.StopAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendNotificationAsync(string text, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var options = new RequestOptions { CancelToken = linked.Token };
        IUser? user = _client.GetUser(_authorizedUserId);
        user ??= await _client.Rest.GetUserAsync(_authorizedUserId, options).ConfigureAwait(false);
        if (user is null) { throw new InvalidOperationException("The Discord recipient could not be reached."); }
        var channel = await user.CreateDMChannelAsync(options).ConfigureAwait(false);
        foreach (var chunk in TextChannelReplyChunker.Split(text, MessageLimit))
        {
            await channel.SendMessageAsync(chunk, options: options).ConfigureAwait(false);
        }
    }

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (_lifetime.IsCancellationRequested || message.Author.Id != _authorizedUserId ||
            message.Author.Id == _client.CurrentUser?.Id)
        {
            return Task.CompletedTask;
        }
        var client = _connection.Transport;
        var attachments = message.Attachments.Select(attachment => new ChannelAttachment(
            attachment.Filename, attachment.ContentType, attachment.Size,
            ct => client.GetStreamAsync(attachment.Url, ct))).ToArray();
        var inbound = new ChannelMessage(message.Content ?? string.Empty,
            message.Author.Id.ToString(CultureInfo.InvariantCulture), message.Author.Username,
            message.Channel.Id.ToString(CultureInfo.InvariantCulture), null, attachments,
            (text, ct) => ReplyAsync(message.Channel, text, ct),
            () => message.Channel.EnterTypingState(), message.Author.IsBot);
        // The host returns after admission so the SDK can receive the pending turn's replies.
        return _connection.Sink.ReceiveAsync(inbound, _lifetime.Token);
    }

    private async Task ReplyAsync(IMessageChannel channel, string text, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        foreach (var chunk in TextChannelReplyChunker.Split(text, MessageLimit))
        {
            await channel.SendMessageAsync(chunk, options: new RequestOptions { CancelToken = linked.Token })
                .ConfigureAwait(false);
        }
    }

    private Task OnReadyAsync() => _connection.Sink.StatusChangedAsync(
        new(ChannelConnectionState.Connected, _client.CurrentUser?.Username));

    private Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is not null) { _logger.LogWarning(exception, "Discord connection was interrupted."); }
        return _connection.Sink.StatusChangedAsync(new(
            exception is null ? ChannelConnectionState.Disconnected : ChannelConnectionState.Error,
            Error: exception is null ? null : "Discord connection was interrupted. Check the connection logs."));
    }

    private Task OnLogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            _ => LogLevel.Debug
        };
        _logger.Log(level, message.Exception, "Discord: {Message}", message.Message);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _client.MessageReceived -= OnMessageReceivedAsync;
        _client.Ready -= OnReadyAsync;
        _client.Disconnected -= OnDisconnectedAsync;
        _client.Log -= OnLogAsync;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _client.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            _connection.Transport.Dispose();
            _lifetime.Dispose();
        }
    }
}
