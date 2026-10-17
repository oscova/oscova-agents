using System;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Channels;

/// <summary>Owns one vendor connection using configuration and callbacks supplied by its host.</summary>
public interface IChannelConnection : IAsyncDisposable
{
    /// <summary>Starts the connection and reports readiness through the status callback.</summary>
    Task StartAsync(CancellationToken cancellationToken);
    /// <summary>Stops receiving and sending through this connection.</summary>
    Task StopAsync(CancellationToken cancellationToken);
    /// <summary>Sends a notification to the configured recipient or throws if delivery fails.</summary>
    Task SendNotificationAsync(string text, CancellationToken cancellationToken);
}
