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
        ILogger? logger,
        string? primaryMountTarget = null)
    {
        var grants = directories.ToArray();
        var mountTargets = new HashSet<string>(StringComparer.Ordinal);
        var mounted = grants.Where(directory => directory.Kind == DirectorySourceKind.LocalPath && Directory.Exists(directory.Value)).ToArray();
        foreach (AllowedDirectory directory in grants)
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

            // Aliases of the same root retain the workspace owner's read-only precedence.
            if (mountTarget == primaryMountTarget) { continue; }
            string accessMode = IsWritable(directory) ? "rw" : "ro";
            args.Add("-v");
            args.Add($"{directory.Value}:{mountTarget}:{accessMode}");
        }

        // Protect a read-only child through every writable ancestor alias, including the primary mount.
        foreach (var parent in mounted.Where(IsWritable))
        {
            var parentTarget = PathHelper.ResolveToAgentPath(parent.Value, [parent]).TrimEnd('/');
            foreach (var child in mounted.Where(directory => !IsWritable(directory)))
            {
                if (PathHelper.ArePathsEqual(parent.Value, child.Value) ||
                    !PathHelper.IsPathWithinDirectory(child.Value, parent.Value)) { continue; }
                var relative = PathHelper.ToSlashSeparatedRelativePath(Path.GetRelativePath(parent.Value, child.Value));
                var target = parentTarget + "/" + relative;
                if (!mountTargets.Add(target)) { continue; }
                args.Add("-v");
                args.Add($"{child.Value}:{target}:ro");
            }
        }

        bool IsWritable(AllowedDirectory directory) =>
            PathHelper.TryGetContainingDirectory(directory.Value, grants, out var effective) &&
            effective?.AccessMode == WorkspaceAccessMode.ReadWrite;
    }
}
