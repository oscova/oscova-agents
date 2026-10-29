using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Oscova.Agents.Host.Services.UI;

/// <summary>Cancels explicit model tests when their browser disconnects, including retained Blazor circuits.</summary>
public sealed class ModelTestCircuitHandler : CircuitHandler, IDisposable
{
    private CancellationTokenSource _connection = new();
    private bool _disposed;
    public CancellationToken Disconnected => _connection.Token;

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_connection.IsCancellationRequested)
        {
            _connection.Dispose();
            _connection = new();
        }

        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken) => _connection.CancelAsync();

    public void Dispose()
    {
        if (_disposed) { return; }

        _disposed = true;
        _connection.Cancel();
        _connection.Dispose();
    }
}
