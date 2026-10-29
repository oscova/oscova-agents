using System.Diagnostics;
using System.Globalization;

namespace Oscova.Agents.Infrastructure.Utils;

/// <summary>Identifies one native process lifetime across independent readers.</summary>
public static class ProcessIdentity
{
    /// <summary>Returns the native process creation marker for cross-process identity checks.</summary>
    public static long GetStartIdentifier(Process process)
    {
        if (!OperatingSystem.IsLinux()) { return process.StartTime.ToUniversalTime().Ticks; }

        // Linux start-time timestamps drift between readers, kernel start ticks identify the same process.
        var stat = File.ReadAllText($"/proc/{process.Id}/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return long.Parse(fields[19], CultureInfo.InvariantCulture);
    }
}
