using Oscova.Agents.Channels;
using SlackNet;
using SlackNet.WebApi;
using SlackWebMessage = SlackNet.WebApi.Message;

namespace Oscova.Agents.Plugins.Slack;

/// <summary>Sends paced Slack replies with safe text escaping and whole chunks.</summary>
internal sealed class SlackReplySender(
    ISlackApiClient api,
    CancellationToken lifetime) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastPost;

    /// <summary>Sends complete escaped text to the original conversation and thread.</summary>
    public async Task SendAsync(string channel, string? threadTs, string text, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var escaped = text.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal);
            var carry = string.Empty;
            foreach (var part in TextChannelReplyChunker.Split(escaped, 3995))
            {
                var chunk = carry + part;
                carry = string.Empty;
                var ampersand = chunk.LastIndexOf('&');
                // Carry at most five entity characters so the next chunk stays within 4,000 units.
                if (ampersand >= 0 && chunk.IndexOf(';', ampersand) < 0)
                {
                    carry = chunk[ampersand..];
                    chunk = chunk[..ampersand];
                }
                if (chunk.Length == 0) { continue; }
                var remaining = _lastPost + TimeSpan.FromSeconds(1) - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero) { await Task.Delay(remaining, linked.Token).ConfigureAwait(false); }
                await api.Chat.PostMessage(new SlackWebMessage
                {
                    Channel = channel,
                    Text = chunk,
                    ThreadTs = threadTs,
                    Parse = ParseMode.None,
                    LinkNames = false,
                    UnfurlLinks = false,
                    UnfurlMedia = false,
                    ReplyBroadcast = false
                }, linked.Token).ConfigureAwait(false);
                _lastPost = DateTimeOffset.UtcNow;
            }
            if (carry.Length > 0) { throw new InvalidOperationException("Slack reply contained an incomplete escape entity."); }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
