namespace Oscova.Agents.Infrastructure.Downloads;

/// <summary>Limits frequent progress notifications while allowing immediate phase changes.</summary>
internal sealed class ProgressNotificationThrottle
{
    private const long IntervalMilliseconds = 250;
    private long _lastNotification;

    /// <summary>Reserves a notification interval or immediately records a status change.</summary>
    internal bool TryNotify(bool force = false)
    {
        var now = Environment.TickCount64;
        if (force)
        {
            Interlocked.Exchange(ref _lastNotification, now);
            return true;
        }

        var last = Interlocked.Read(ref _lastNotification);
        return now - last >= IntervalMilliseconds &&
               Interlocked.CompareExchange(ref _lastNotification, now, last) == last;
    }
}
