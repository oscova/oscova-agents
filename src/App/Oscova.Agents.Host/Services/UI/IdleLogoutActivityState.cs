namespace Oscova.Agents.Host.Services.UI;

/// <summary>
/// Signals accepted message submissions to this circuit's idle-logout monitor.
/// </summary>
public sealed class IdleLogoutActivityState
{
    public event EventHandler? Submitted;

    /// <summary>
    /// Notifies the monitor that a human message was accepted for submission.
    /// </summary>
    public void RecordSubmit() => Submitted?.Invoke(this, EventArgs.Empty);
}
