using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Utils;

/// <summary>
/// Path security utilities for containment checks and safe logging.
/// </summary>
public static class PathHelper
{
    /// <summary>Returns whether a value is one non-empty path segment on the current platform.</summary>
    internal static bool IsSafePathSegment(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value is not "." and not ".." &&
               !Path.IsPathRooted(value) &&
               value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    /// <summary>Resolves a relative path only when it remains inside the supplied directory without link hops.</summary>
    internal static bool TryResolveContainedPath(string directoryPath, string relativePath, out string fullPath)
    {
        var normalizedDirectory = Path.GetFullPath(directoryPath);
        fullPath = Path.GetFullPath(Path.Combine(normalizedDirectory, relativePath));
        if (IsPathWithinDirectory(fullPath, normalizedDirectory) &&
            !HasSymlinkInPath(fullPath, normalizedDirectory))
        {
            return true;
        }

        fullPath = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="fullPath"/> is located inside
    /// <paramref name="normalizedDirectoryPath"/>. Uses <see cref="Path.GetRelativePath"/>
    /// which is natively case-sensitive on Linux/macOS and case-insensitive on Windows.
    /// </summary>
    public static bool IsPathWithinDirectory(string fullPath, string normalizedDirectoryPath)
    {
        string relative = Path.GetRelativePath(normalizedDirectoryPath, fullPath);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="fullPath"/> is located inside any
    /// of the supplied directory roots.
    /// </summary>
    public static bool IsPathWithinAnyDirectory(string fullPath, IReadOnlyList<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);

        foreach (string allowedRoot in allowedRoots)
        {
            if (IsPathWithinDirectory(fullPath, allowedRoot))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when two paths resolve to the same filesystem location.
    /// Uses the platform's default path comparison rules.
    /// </summary>
    public static bool ArePathsEqual(string firstPath, string secondPath)
    {
        if (string.IsNullOrWhiteSpace(firstPath) || string.IsNullOrWhiteSpace(secondPath))
        {
            return false;
        }

        string firstFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(firstPath));
        string secondFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(secondPath));

        return Path.GetRelativePath(firstFullPath, secondFullPath) == ".";
    }

    /// <summary>
    /// Converts a relative path to the forward-slash form used by skill files, zip entries, and model-facing paths.
    /// </summary>
    public static string ToSlashSeparatedRelativePath(string relativePath)
    {
        return relativePath.Replace('\\', '/');
    }

    /// <summary>Returns a path relative to a named root, or <see langword="null"/> when it lies outside that root.</summary>
    public static string? GetRelativePathUnderRoot(
        string relativePath,
        string relativeRoot,
        StringComparison comparison)
    {
        string path = ToSlashSeparatedRelativePath(relativePath).Trim('/');
        string root = ToSlashSeparatedRelativePath(relativeRoot).Trim('/');
        if (string.Equals(path, root, comparison))
        {
            return string.Empty;
        }

        if (!path.StartsWith(root + "/", comparison))
        {
            return null;
        }

        string candidate = path[(root.Length + 1)..];
        // A textual prefix can still escape the logical root through a traversal segment.
        return candidate.Split('/').Contains("..", StringComparer.Ordinal) ? null : candidate;
    }

    /// <summary>
    /// Converts a slash-separated relative path into the current platform's directory-separator form.
    /// </summary>
    public static string ToPlatformRelativePath(string relativePath)
    {
        return relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Builds the agent-facing mount path for an extra (non-workspace, non-data) allowed directory,
    /// e.g. <c>/mnt/chat-attachments-for-this-thread</c>. Single owner of the <c>/mnt/&lt;label&gt;</c> rule.
    /// </summary>
    public static string ToExtraMountAgentPath(string label)
    {
        return "/mnt/" + SanitizeMountName(label);
    }

    /// <summary>
    /// Builds the agent-facing mount path for a named workspace, e.g. <c>/workspace/default</c>.
    /// Single owner of the <c>/workspace/&lt;name&gt;</c> rule.
    /// </summary>
    public static string ToWorkspaceAgentPath(string workspaceName)
    {
        return "/workspace/" + SanitizeMountName(workspaceName);
    }

    /// <summary>
    /// Converts arbitrary workspace labels into stable container mount names.
    /// </summary>
    public static string SanitizeMountName(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "workspace";
        }

        var builder = new System.Text.StringBuilder(label.Length);
        bool lastWasSeparator = false;

        foreach (char character in label.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSeparator = false;
                continue;
            }

            if (!lastWasSeparator)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        string sanitized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "workspace" : sanitized;
    }

    /// <summary>
    /// Returns the host directory that acts as the working directory for relative agent paths.
    /// </summary>
    public static string? ResolveWorkingDirectoryHostPath(IReadOnlyList<AllowedDirectory> allowedDirectories)
    {
        return FindWorkingDirectory(allowedDirectories)?.Value;
    }

    /// <summary>
    /// Returns the agent-facing path of the working directory (e.g. <c>/workspace/default</c> or a
    /// named workspace in sandbox mode). Use this wherever the shell or system prompt must advertise
    /// a working directory — it matches what the shell is actually mounted at.
    /// </summary>
    public static string? ResolveWorkingDirectoryAgentPath(IReadOnlyList<AllowedDirectory> allowedDirectories)
    {
        AllowedDirectory? dir = FindWorkingDirectory(allowedDirectories);
        return dir?.EffectiveAgentPath;
    }

    private static AllowedDirectory? FindWorkingDirectory(IReadOnlyList<AllowedDirectory> allowedDirectories)
    {
        ArgumentNullException.ThrowIfNull(allowedDirectories);

        AllowedDirectory? defaultWorkspace = allowedDirectories.FirstOrDefault(directory =>
            string.Equals(directory.Label, "default", StringComparison.OrdinalIgnoreCase) &&
            directory.Kind == DirectorySourceKind.LocalPath &&
            directory.AccessMode == WorkspaceAccessMode.ReadWrite);
        if (!string.IsNullOrWhiteSpace(defaultWorkspace?.Value))
        {
            return defaultWorkspace;
        }

        AllowedDirectory? writableDirectory = allowedDirectories.FirstOrDefault(directory =>
            directory.Kind == DirectorySourceKind.LocalPath &&
            directory.AccessMode == WorkspaceAccessMode.ReadWrite);

        return string.IsNullOrWhiteSpace(writableDirectory?.Value) ? null : writableDirectory;
    }

    /// <summary>
    /// THE PATH CONTRACT. Every filesystem-facing tool (read, write, list, PDF, and any future or
    /// plugin tool) resolves an agent-supplied path to a host path through this one method, then does
    /// its IO on the host. The agent is only ever shown agent-facing paths:
    /// <list type="bullet">
    ///   <item>sandbox mode: <c>/workspace/&lt;name&gt;</c>, <c>/data</c>, <c>/mnt/&lt;label&gt;</c>;</item>
    ///   <item>host / disabled mode: the real host paths of the OS the app runs on.</item>
    /// </list>
    /// In sandbox mode those agent-facing roots are bind-mounted into the container at exactly these
    /// paths, so a file tool (host IO) and the sandboxed shell see the <b>same bytes</b>. Relative paths
    /// resolve against the working directory in every mode. The argument is never mutated, so tool
    /// output keeps showing the agent the path it passed.
    /// <para>
    /// The invariant that keeps both surfaces consistent: <b>never present the agent a path that does
    /// not resolve here / is not mounted into the sandbox.</b> Any new tool must resolve through this
    /// method (or <see cref="ResolveToHostPath"/>) rather than calling <c>System.IO</c> on a raw argument.
    /// </para>
    /// </summary>
    public static bool TryResolveToHostPath(
        string requestedPath,
        IReadOnlyList<AllowedDirectory> allowedDirectories,
        string? workingDirectoryHostPath,
        out string hostFullPath)
    {
        ArgumentNullException.ThrowIfNull(allowedDirectories);

        hostFullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return false;
        }

        string normalized = ToSlashSeparatedRelativePath(requestedPath);

        foreach (AllowedDirectory directory in allowedDirectories)
        {
            if (directory.Kind != DirectorySourceKind.LocalPath)
            {
                continue;
            }

            string agentPath = ToSlashSeparatedRelativePath(directory.EffectiveAgentPath).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(agentPath))
            {
                continue;
            }

            if (string.Equals(normalized, agentPath, StringComparison.Ordinal) ||
                normalized.StartsWith(agentPath + "/", StringComparison.Ordinal))
            {
                string suffix = normalized.Length == agentPath.Length ? string.Empty : normalized[(agentPath.Length + 1)..];
                hostFullPath = Path.GetFullPath(Path.Combine(directory.Value, ToPlatformRelativePath(suffix)));
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(workingDirectoryHostPath) &&
            (string.Equals(normalized, "/workspace", StringComparison.Ordinal) ||
             normalized.StartsWith("/workspace/", StringComparison.Ordinal)))
        {
            // Treat bare /workspace paths as shorthand for the resolved shell working directory.
            string suffix = normalized.Length == "/workspace".Length ? string.Empty : normalized["/workspace/".Length..];
            hostFullPath = Path.GetFullPath(Path.Combine(workingDirectoryHostPath, ToPlatformRelativePath(suffix)));
            return true;
        }

        if (Path.IsPathFullyQualified(requestedPath))
        {
            hostFullPath = Path.GetFullPath(requestedPath);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(workingDirectoryHostPath))
        {
            hostFullPath = Path.GetFullPath(Path.Combine(workingDirectoryHostPath, ToPlatformRelativePath(normalized)));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Converts a resolved host path back to the most specific agent-facing allowed-directory path.
    /// </summary>
    public static bool TryResolveToAgentPath(
        string hostPath,
        IReadOnlyList<AllowedDirectory> allowedDirectories,
        out string agentPath)
    {
        ArgumentNullException.ThrowIfNull(allowedDirectories);

        agentPath = string.Empty;
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return false;
        }

        string hostFullPath = Path.GetFullPath(hostPath);
        if (allowedDirectories.Count == 0)
        {
            agentPath = hostFullPath;
            return true;
        }

        if (!TryGetContainingDirectory(hostFullPath, allowedDirectories, out AllowedDirectory? containingDirectory) ||
            containingDirectory is null)
        {
            return false;
        }

        string relativePath = Path.GetRelativePath(containingDirectory.Value, hostFullPath);
        if (string.IsNullOrWhiteSpace(containingDirectory.AgentPath))
        {
            agentPath = hostFullPath;
            return true;
        }

        string agentRoot = ToSlashSeparatedRelativePath(containingDirectory.AgentPath).TrimEnd('/');
        agentPath = relativePath == "."
            ? agentRoot
            : agentRoot + "/" + ToSlashSeparatedRelativePath(relativePath);
        return true;
    }

    /// <summary>
    /// Converts a resolved host path to the active run's agent-facing path or throws a tool-friendly error.
    /// </summary>
    public static string ResolveToAgentPath(
        string hostPath,
        IReadOnlyList<AllowedDirectory> allowedDirectories)
    {
        return TryResolveToAgentPath(hostPath, allowedDirectories, out string agentPath)
            ? agentPath
            : throw new InvalidOperationException("Host path could not be mapped to an allowed workspace directory.");
    }

    /// <summary>
    /// Resolves an agent-supplied path through a live allowed-directory resolver or throws a tool-friendly error.
    /// </summary>
    public static string ResolveToHostPath(
        string requestedPath,
        Func<IReadOnlyList<AllowedDirectory>> allowedDirectoriesResolver)
    {
        ArgumentNullException.ThrowIfNull(allowedDirectoriesResolver);

        IReadOnlyList<AllowedDirectory> allowedDirectories = allowedDirectoriesResolver();
        string? workingDirectory = ResolveWorkingDirectoryHostPath(allowedDirectories);

        if (TryResolveToHostPath(requestedPath, allowedDirectories, workingDirectory, out string hostPath))
        {
            return hostPath;
        }

        throw new InvalidOperationException($"Path could not be resolved against the allowed workspace directories: {requestedPath}");
    }

    /// <summary>
    /// Resolves and authorizes an agent path against current directory grants, including symlink and access checks.
    /// </summary>
    public static string ResolveAuthorizedHostPath(
        string requestedPath,
        IReadOnlyList<AllowedDirectory> allowedDirectories,
        WorkspacePathAccess requiredAccess)
    {
        ArgumentNullException.ThrowIfNull(allowedDirectories);

        if (!TryResolveToHostPath(
                requestedPath,
                allowedDirectories,
                ResolveWorkingDirectoryHostPath(allowedDirectories),
                out string fullPath) ||
            !TryGetContainingDirectory(fullPath, allowedDirectories, out AllowedDirectory? containingDirectory) ||
            containingDirectory is null)
        {
            throw new UnauthorizedAccessException($"Path is outside the allowed workspace directories: {requestedPath}");
        }

        if (HasSymlinkInPath(fullPath, containingDirectory.Value))
        {
            throw new UnauthorizedAccessException($"Path contains a symlink or reparse point: {requestedPath}");
        }

        if (requiredAccess == WorkspacePathAccess.Write &&
            containingDirectory.AccessMode != WorkspaceAccessMode.ReadWrite)
        {
            throw new UnauthorizedAccessException($"Path is read-only for this agent: {requestedPath}");
        }

        return fullPath;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="relativePath"/> is a direct file child of
    /// any supplied top-level directory name.
    /// </summary>
    public static bool IsDirectChildOfAnyDirectory(string relativePath, IEnumerable<string> directoryNames)
    {
        string normalizedPath = ToSlashSeparatedRelativePath(relativePath);

        foreach (string directoryName in directoryNames)
        {
            string directoryPrefix = ToSlashSeparatedRelativePath(directoryName).TrimEnd('/') + "/";
            if (!normalizedPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string childPath = normalizedPath[directoryPrefix.Length..];
            return childPath.Length > 0 && !childPath.Contains('/');
        }

        return false;
    }

    /// <summary>
    /// Finds the most specific allowed directory that contains <paramref name="fullPath"/>.
    /// </summary>
    public static bool TryGetContainingDirectory(
        string fullPath,
        IReadOnlyList<AllowedDirectory> allowedDirectories,
        out AllowedDirectory? containingDirectory)
    {
        ArgumentNullException.ThrowIfNull(allowedDirectories);

        AllowedDirectory? bestMatch = null;
        int bestMatchLength = -1;

        foreach (AllowedDirectory allowedDirectory in allowedDirectories)
        {
            if (IsPathWithinDirectory(fullPath, allowedDirectory.Value))
            {
                string normalizedAllowedPath = Path.GetFullPath(allowedDirectory.Value)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                int candidateLength = normalizedAllowedPath.Length;

                // More specific roots win so nested workspace rules do not depend on list order.
                if (candidateLength > bestMatchLength)
                {
                    bestMatch = allowedDirectory;
                    bestMatchLength = candidateLength;
                    continue;
                }

                // Equal roots can only differ by metadata. Prefer ReadOnly so ties fail safe.
                if (candidateLength == bestMatchLength &&
                    bestMatch is not null &&
                    bestMatch.AccessMode == WorkspaceAccessMode.ReadWrite &&
                    allowedDirectory.AccessMode == WorkspaceAccessMode.ReadOnly)
                {
                    bestMatch = allowedDirectory;
                }
            }
        }

        containingDirectory = bestMatch;
        return bestMatch is not null;
    }

    /// <summary>
    /// Returns <see langword="true"/> if any path segment beneath
    /// <paramref name="normalizedDirectoryPath"/> is a symlink or reparse point.
    /// Inaccessible segments are treated conservatively as unsafe.
    /// </summary>
    public static bool HasSymlinkInPath(string fullPath, string normalizedDirectoryPath)
    {
        string relativePath = fullPath.Substring(normalizedDirectoryPath.Length);
        string[] segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        string currentPath = normalizedDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (string segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);

            try
            {
                if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
                // Path segment does not exist yet — cannot be a symlink, safe to continue.
                            }
            catch (DirectoryNotFoundException)
            {
                // A parent directory does not exist — cannot be a symlink, safe to continue.
                            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (IOException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces control characters in a file path with <c>'?'</c> to prevent log injection
    /// via crafted filenames containing newlines or other control characters.
    /// </summary>
    public static string SanitizeForLog(string path)
    {
        char[]? chars = null;
        for (int index = 0; index < path.Length; index++)
        {
            if (char.IsControl(path[index]))
            {
                chars ??= path.ToCharArray();
                chars[index] = '?';
            }
        }

        return chars is null ? path : new string(chars);
    }
}
