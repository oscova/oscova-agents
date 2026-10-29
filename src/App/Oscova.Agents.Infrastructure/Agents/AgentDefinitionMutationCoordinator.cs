namespace Oscova.Agents.Infrastructure.Agents;

/// <summary>Serializes file-backed agent mutations across scoped repository instances.</summary>
public sealed class AgentDefinitionMutationCoordinator : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Runs one agent mutation without overlapping another repository scope.</summary>
    public async Task RunAsync(Func<CancellationToken, Task> mutationAsync, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await mutationAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }
}
