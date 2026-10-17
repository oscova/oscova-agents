using Microsoft.Extensions.Logging;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Plugins.Docker;

/// <summary>
/// Formats Docker -v arguments for agent-facing directory mounts.
/// Single owner of the host-to-agent bind-mount formatting shared by the shell sandbox and containerized MCP.
/// </summary>
internal static class SandboxMountArgs
{
    public static void AppendDirectoryMounts(
        List<string> args,
        IEnumerable<AllowedDirectory> directories,
        ILogger? logger)
    {
        var mountTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (AllowedDirectory directory in directories)
        {
            if (directory.Kind != DirectorySourceKind.LocalPath)
            {
                continue;
            }

            if (!Directory.Exists(directory.Value))
            {
                logger?.LogWarning(
                    "Skipping sandbox mount for missing workspace directory '{DirectoryPath}' with label '{DirectoryLabel}'.",
                    directory.Value,
                    directory.Label);
                continue;
            }

            string mountTarget = PathHelper.ResolveToAgentPath(directory.Value, [directory]);
            if (!mountTargets.Add(mountTarget))
            {
                throw new InvalidOperationException($"Workspace directory label '{directory.Label}' resolves to duplicate mount target '{mountTarget}'.");
            }

            string accessMode = directory.AccessMode == WorkspaceAccessMode.ReadWrite ? "rw" : "ro";
            args.Add("-v");
            args.Add($"{directory.Value}:{mountTarget}:{accessMode}");
        }
    }
}
