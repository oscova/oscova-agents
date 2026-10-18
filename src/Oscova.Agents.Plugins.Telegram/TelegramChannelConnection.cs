using System.Globalization;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Channels;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Oscova.Agents.Plugins.Telegram;

/// <summary>Translates private Telegram messages into channel input and owns the polling lifetime.</summary>
public sealed class TelegramChannelConnection : IChannelConnection
{
    private const int MessageLimit = 4096;
    private readonly AgentPluginContext _services;
    private readonly ChannelConnectionContext _connection;
    private readonly ILogger _logger;
    private readonly long _authorizedUserId;
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _lifetime = new();
    private TelegramBotClient? _client;
    private TelegramBotClientOptions? _options;
    private Task _polling = Task.CompletedTask;
    private string? _botUsername;
    private bool _pollingHadError;
    private bool _disposed;

    /// <summary>Creates a disconnected transport from an immutable configuration snapshot.</summary>
    public TelegramChannelConnection(AgentPluginContext services, ChannelConnectionContext connection, TelegramSettings settings)
    {
        _services = services;
        _connection = connection;
        _httpClient = connection.Transport;
        _logger = services.LoggerFactory.CreateLogger<TelegramChannelConnection>();
        _authorizedUserId = long.Parse(settings.AuthorizedUserId, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var token = await _services.CredentialResolver.ResolveAsync(
            TelegramChannel.Credential.Key, TelegramChannel.BotToken, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token)) { throw new InvalidOperationException("Save the Telegram bot token in Credentials before starting."); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            _options = new TelegramBotClientOptions(token);
            _client = new TelegramBotClient(_options, _httpClient);
            var bot = await _client.GetMe(linked.Token).ConfigureAwait(false);
            _botUsername = bot.Username;
            await _connection.Sink.StatusChangedAsync(new(ChannelConnectionState.Connected, _botUsername)).ConfigureAwait(false);
            _polling = _client.ReceiveAsync((_, update, ct) => OnUpdateAsync(update, ct),
                (_, error, ct) => OnPollingErrorAsync(error, ct),
                new ReceiverOptions { AllowedUpdates = [UpdateType.Message] }, _lifetime.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogFailure(ex, "start");
            // Vendor exception messages can contain token-bearing request URLs.
            throw new InvalidOperationException("Telegram could not start. Check the bot token and connectivity.");
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed) { return; }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _polling.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    /// <inheritdoc />
    public Task SendNotificationAsync(string text, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Telegram supports private chat only.");

    private async Task OnUpdateAsync(Update update, CancellationToken ct)
    {
        if (_lifetime.IsCancellationRequested) { return; }
        if (_pollingHadError)
        {
            // A received update confirms polling recovery independently of sender authorization.
            await _connection.Sink.StatusChangedAsync(new(ChannelConnectionState.Connected, _botUsername)).ConfigureAwait(false);
            _pollingHadError = false;
        }

        if (update.Message is not { } message ||
            message.Chat.Type != ChatType.Private || message.From?.Id != _authorizedUserId)
        {
            return;
        }
        var inbound = new ChannelMessage(message.Text ?? message.Caption ?? string.Empty,
            message.From.Id.ToString(CultureInfo.InvariantCulture), message.From.Username ?? message.From.FirstName,
            message.Chat.Id.ToString(CultureInfo.InvariantCulture), null, GetAttachments(message),
            (text, token) => ReplyAsync(message.Chat.Id, text, token), IsBot: message.From.IsBot);
        // The host admits work before returning, keeping polling free for approval replies.
        await _connection.Sink.ReceiveAsync(inbound, ct).ConfigureAwait(false);
    }

    private IReadOnlyList<ChannelAttachment> GetAttachments(Message message)
    {
        var attachments = new List<ChannelAttachment>();
        if (message.Document is { } document)
        {
            attachments.Add(new(
                string.IsNullOrWhiteSpace(document.FileName) ? $"telegram-document-{message.MessageId}" : document.FileName,
                document.MimeType ?? "application/octet-stream", document.FileSize ?? 0,
                ct => OpenReadAsync(document.FileId, ct)));
        }
        if (message.Photo is { Length: > 0 } photos)
        {
            var photo = photos.OrderByDescending(item => item.FileSize ?? 0)
                .ThenByDescending(item => (long)item.Width * item.Height).First();
            attachments.Add(new($"telegram-photo-{message.MessageId}.jpg", "image/jpeg", photo.FileSize ?? 0,
                ct => OpenReadAsync(photo.FileId, ct)));
        }
        return attachments;
    }

    private async Task<Stream> OpenReadAsync(string fileId, CancellationToken ct)
    {
        try
        {
            // Metadata stays inert until the host has authorized the incoming attachment.
            var file = await _client!.GetFile(fileId, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(file.FilePath)) { throw new InvalidOperationException("Telegram returned no file path."); }
            return await _httpClient.GetStreamAsync($"{_options!.BaseFileUrl}/{file.FilePath}", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogFailure(ex, "download", fileId);
            throw new IOException("Telegram attachment could not be downloaded. Check connectivity and retry.");
        }
    }

    private async Task ReplyAsync(long chatId, string text, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            foreach (var chunk in TextChannelReplyChunker.Split(text, MessageLimit))
            {
                await _client!.SendMessage(chatId, chunk, cancellationToken: linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogFailure(ex, "reply", chatId.ToString(CultureInfo.InvariantCulture));
            // Reply errors cross the host logging boundary, so omit token-bearing vendor details.
            throw new IOException("Telegram reply could not be sent. Check connectivity and the bot's chat access.");
        }
    }

    private Task OnPollingErrorAsync(Exception error, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) { return Task.CompletedTask; }
        _pollingHadError = true;
        LogFailure(error, "poll");
        return _connection.Sink.StatusChangedAsync(new(ChannelConnectionState.Error,
            Error: "Telegram polling failed. Check the bot token and connectivity."));
    }

    private void LogFailure(Exception error, string operation, string? target = null)
    {
        var status = error is ApiRequestException api ? api.ErrorCode :
            error is HttpRequestException http ? (int?)http.StatusCode : null;
        _logger.LogError("Telegram {Operation} failed for {Target}: {ErrorType}, status {Status}.",
            operation, target, error.GetType().Name, status);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            _httpClient.Dispose();
            _lifetime.Dispose();
            _disposed = true;
        }
    }
}
