using System.Diagnostics;

namespace Oscova.Agents.App.Cli;

/// <summary>
/// Shared process-tree termination used when a local host must be cleaned up outside HTTP control.
/// </summary>
public static class ProcessControl
{
    /// <summary>
    /// Terminates a process and its children when the process is still running.
    /// </summary>
    public static void KillTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
