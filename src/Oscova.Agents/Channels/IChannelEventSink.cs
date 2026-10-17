using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Channels;

/// <summary>Receives channel events, with message completion meaning admission rather than turn completion.</summary>
public interface IChannelEventSink
{
    /// <summary>Admits a sender-authorized message for a Host turn.</summary>
    Task ReceiveAsync(ChannelMessage message, CancellationToken cancellationToken);
    /// <summary>Publishes a connection status change.</summary>
    Task StatusChangedAsync(ChannelConnectionStatus status);
}
