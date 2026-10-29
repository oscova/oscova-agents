namespace Oscova.Agents.Infrastructure.Utils;

/// <summary>
/// Resolves executable paths from the host process PATH for callers that need to confirm a runtime exists before launch.
/// </summary>
public static class ExecutableLocator
{
    /// <summary>
    /// Returns the first matching executable path from PATH, including common Windows executable extensions.
    /// </summary>
    public static string? FindOnPath(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        executable = executable.Trim().Trim('"');
        if (executable.Length == 0)
        {
            return null;
        }

        if (Path.IsPathRooted(executable) ||
            executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(executable) ? executable : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? [".exe", ".cmd", ".bat", ".com", string.Empty]
            : [string.Empty];
        foreach (var rawDirectory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, executable + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
