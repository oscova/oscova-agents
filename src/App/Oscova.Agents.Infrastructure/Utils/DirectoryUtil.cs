namespace Oscova.Agents.Infrastructure.Utils;

/// <summary>Shares bounded directory-move retries across infrastructure operations.</summary>
internal static class DirectoryUtil
{
    /// <summary>Retries a directory move briefly before propagating the failure.</summary>
    internal static void MoveWithRetry(string source, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 3)
            {
                // Give short-lived filesystem contention time to clear before failing.
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * (attempt + 1)));
            }
        }
    }
}
