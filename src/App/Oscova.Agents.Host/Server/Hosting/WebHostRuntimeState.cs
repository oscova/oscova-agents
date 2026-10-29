namespace Oscova.Agents.Host.Server.Hosting;

/// <summary>
/// Captures the listener URLs selected during startup so settings APIs can report active binding state.
/// </summary>
public sealed class WebHostRuntimeState
{
    public IReadOnlyList<string> ActiveUrls { get; private set; } = [];
    public NetworkBindingOverride? ActiveBinding { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Stores the current listener URL snapshot for later API/UI reporting.
    /// </summary>
    public void Update(IEnumerable<string> activeUrls, NetworkBindingOverride? activeBinding = null)
    {
        ActiveUrls = [.. activeUrls];
        ActiveBinding = activeBinding;
        StartedAtUtc = DateTimeOffset.UtcNow;
    }
}
