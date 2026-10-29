namespace Oscova.Agents.Host.Services.UI;

/// <summary>Supplies a circuit-local cache-busting version and browser-image notification.</summary>
public abstract class AssetRefreshState
{
    public event EventHandler? Changed;

    public int Version { get; private set; }

    /// <summary>Advances the cache-busting version and notifies the current circuit.</summary>
    public void Refresh()
    {
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
