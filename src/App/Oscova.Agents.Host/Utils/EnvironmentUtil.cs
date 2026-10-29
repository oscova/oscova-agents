namespace Oscova.Agents.Host.Utils
{
    /// <summary>
    /// Environment helpers.
    /// </summary>
    public static class EnvironmentUtil
    {
        private static readonly Lazy<bool> CachedIsRunningInContainer = new(CheckIsRunningInContainer);

        /// <summary>
        /// True when the current process is running inside a container (Docker/Podman/Kubernetes/etc.).
        /// </summary>
        public static bool IsRunningInContainer => CachedIsRunningInContainer.Value;

        /// <summary>Returns current process environment-variable names only.</summary>
        public static IReadOnlyList<string> GetEnvironmentVariableNames() =>
            [.. Environment.GetEnvironmentVariables().Keys
                .Cast<string>()
                .OrderBy(name => name, StringComparer.Ordinal)];

        private static bool CheckIsRunningInContainer()
        {
            try
            {
                // 1) Official .NET images set this
                var value = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
                if (!string.IsNullOrWhiteSpace(value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 2) Some distros/tools set a generic flag
                value = Environment.GetEnvironmentVariable("CONTAINER");
                if (!string.IsNullOrWhiteSpace(value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 3) Podman/CRI-O sentinel
                if (File.Exists("/run/.containerenv"))
                {
                    return true;
                }

                // 4) Docker sentinel
                if (File.Exists("/.dockerenv"))
                {
                    return true;
                }

                // 5) cgroup hints (Linux)
                const string cgroupPath = "/proc/1/cgroup";
                if (File.Exists(cgroupPath))
                {
                    var text = File.ReadAllText(cgroupPath);
                    if (text.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("kubepods", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("containerd", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("podman", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore and fall back to false
            }

            return false;
        }
    }
}
