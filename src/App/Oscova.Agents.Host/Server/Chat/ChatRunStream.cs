using System.Threading.Channels;
using Microsoft.Agents.AI;

namespace Oscova.Agents.Host.Server.Chat;

/// <summary>Buffers one accepted chat run for its initiating AG-UI viewer.</summary>
internal sealed class ChatRunStream
{
    private readonly Channel<AgentResponseUpdate> _channel =
        Channel.CreateUnbounded<AgentResponseUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public ChannelReader<AgentResponseUpdate> Reader => _channel.Reader;

    internal Exception? Failure { get; private set; }

    public void Publish(AgentResponseUpdate update) =>
        _channel.Writer.TryWrite(update);

    public void Detach() =>
        _channel.Writer.TryComplete();

    public void Complete(Exception? failure = null)
    {
        // Retain the cause when the stream formatter turns it into a generic error event.
        Failure = failure;
        _channel.Writer.TryComplete(failure);
    }
}
