using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>Returns a normalized path key using the executing platform's default path comparison rules.</summary>
    public static string GetPathComparisonKey(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
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
    /// Builds the agent-facing mount path for an extra (non-workspace, non-private) allowed directory,
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

    /// <summary>Returns the canonical working-directory path, falling back to its native path for grants without an alias.</summary>
    public static string? ResolveWorkingDirectoryAgentPath(IReadOnlyList<AllowedDirectory> allowedDirectories)
    {
        AllowedDirectory? dir = FindWorkingDirectory(allowedDirectories);
        return dir is null ? null : ResolveToAgentPath(dir.Value, [dir]);
    }

    private static AllowedDirectory? FindWorkingDirectory(IReadOnlyList<AllowedDirectory> allowedDirectories)
    {
        ArgumentNullException.ThrowIfNull(allowedDirectories);

        var marked = allowedDirectories.Where(directory => directory.IsWorkingDirectory).ToArray();
        if (marked.Length > 1)
        {
            throw new InvalidOperationException("Only one working directory can be selected.");
        }
        if (marked.Length == 1)
        {
            var selected = marked[0];
            if (selected.Kind != DirectorySourceKind.LocalPath ||
                !Path.IsPathFullyQualified(selected.Value) || !Directory.Exists(selected.Value) ||
                HasSymlinkInPath(selected.Value, Path.GetPathRoot(selected.Value)!) ||
                !TryGetContainingDirectory(selected.Value, allowedDirectories, out var effective) ||
                effective?.AccessMode != WorkspaceAccessMode.ReadWrite)
            {
                throw new UnauthorizedAccessException("The selected working directory is unavailable or read-only. Restore its access before continuing.");
            }
            return selected;
        }

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
    /// THE PATH CONTRACT. This method translates agent-facing paths and checks logical workspace
    /// authorization. Physical workspace IO must use <see cref="Workspace.IWorkspaceFileAccess"/>
    /// so authorization and filesystem access stay bound to held handles. The agent is only ever
    /// shown paths that resolve through this contract:
    /// <list type="bullet">
    ///   <item>sandbox mode: <c>/workspace/&lt;name&gt;</c>, <c>/private</c>, <c>/mnt/&lt;label&gt;</c>;</item>
    ///   <item>host / disabled mode: the real host paths plus stable file-tool aliases.</item>
    /// </list>
    /// In sandbox mode those agent-facing roots are bind-mounted into the container at exactly these
    /// paths, so a file tool (host IO) and the sandboxed shell see the <b>same bytes</b>. Relative paths
    /// resolve against the working directory in every mode. The argument is never mutated, so tool
    /// output keeps showing the agent the path it passed.
    /// <para>
    /// The invariant that keeps both surfaces consistent: <b>never present the agent a path that does
    /// not resolve here / is not mounted into the sandbox.</b> Tools may use this method for translation
    /// or validation, but must use <see cref="Workspace.IWorkspaceFileAccess"/> for physical file access.
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

        string normalized = NormalizeAgentPath(requestedPath);
        string logicalPath = normalized.TrimStart('/');
        string? matchedHostPath = null;
        foreach (var directory in GetAgentPathRoots(requestedPath, allowedDirectories))
        {
            string alias = NormalizeAgentPath(directory.AgentPath!).Trim('/');
            string? suffix = GetRelativePathUnderRoot(logicalPath, alias, StringComparison.Ordinal);
            if (suffix is null)
            {
                return false;
            }

            string candidate = Path.GetFullPath(Path.Combine(directory.Value, ToPlatformRelativePath(suffix)));
            if (matchedHostPath is not null && !ArePathsEqual(matchedHostPath, candidate))
            {
                return false;
            }

            matchedHostPath = candidate;
        }

        if (matchedHostPath is not null)
        {
            hostFullPath = matchedHostPath;
            return true;
        }

        if (Path.IsPathFullyQualified(requestedPath))
        {
            string nativePath = Path.GetFullPath(requestedPath);
            // A granted native folder can sit under a reserved name without overlapping an alias.
            if (IsReservedLogicalPath(logicalPath) &&
                !TryGetContainingDirectory(nativePath, allowedDirectories, out _))
            {
                return false;
            }

            hostFullPath = nativePath;
            return true;
        }

        if (IsReservedLogicalPath(logicalPath) || Path.IsPathRooted(requestedPath))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(workingDirectoryHostPath))
        {
            hostFullPath = Path.GetFullPath(Path.Combine(workingDirectoryHostPath, ToPlatformRelativePath(normalized)));
            return true;
        }

        return false;
    }

    /// <summary>Matches the most specific granted alias roots without interpreting another device's native paths.</summary>
    public static IReadOnlyList<AllowedDirectory> GetAgentPathRoots(
        string requestedPath, IReadOnlyList<AllowedDirectory> allowedDirectories)
    {
        string logicalPath = NormalizeAgentPath(requestedPath).TrimStart('/');
        var matches = new List<AllowedDirectory>();
        int longest = -1;
        foreach (var directory in allowedDirectories)
        {
            if (directory.Kind != DirectorySourceKind.LocalPath || string.IsNullOrWhiteSpace(directory.AgentPath)) { continue; }
            string alias = NormalizeAgentPath(directory.AgentPath).Trim('/');
            if (logicalPath != alias && !logicalPath.StartsWith(alias + "/", StringComparison.Ordinal)) { continue; }
            if (alias.Length < longest) { continue; }
            if (alias.Length > longest) { matches.Clear(); longest = alias.Length; }
            matches.Add(directory);
        }
        return matches;
    }

    /// <summary>Normalizes separators and no-op segments without resolving parent traversal.</summary>
    private static string NormalizeAgentPath(string path)
    {
        string slashPath = ToSlashSeparatedRelativePath(path);
        string prefix = slashPath.StartsWith("/", StringComparison.Ordinal) ? "/" : string.Empty;
        string[] segments = slashPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(static segment => segment != ".")
            .ToArray();
        return prefix + string.Join('/', segments);
    }

    /// <summary>Returns whether an unmatched path claims one of the logical file-tool roots.</summary>
    private static bool IsReservedLogicalPath(string path)
    {
        return path is "workspace" or "private" or "mnt" ||
               path.StartsWith("workspace/", StringComparison.Ordinal) ||
               path.StartsWith("private/", StringComparison.Ordinal) ||
               path.StartsWith("mnt/", StringComparison.Ordinal);
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

    /// <summary>Names each granted root consistently for prompt references and child-process environment variables.</summary>
    public static IReadOnlyDictionary<string, AllowedDirectory> GetShellRootVariables(IReadOnlyList<AllowedDirectory> directories)
    {
        var result = new Dictionary<string, AllowedDirectory>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < directories.Count; index++)
        {
            var directory = directories[index];
            if (string.IsNullOrWhiteSpace(directory.AgentPath)) { continue; }
            string alias = directory.AgentPath.Trim('/');
            string name = "ROOT_" + alias.Replace('/', '_').ToUpperInvariant();
            if (name.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '_') || result.ContainsKey(name))
            {
                name = "ROOT_EXTRA_" + index.ToString(CultureInfo.InvariantCulture);
                while (result.ContainsKey(name)) { name += "_"; }
            }
            result.Add(name, directory);
        }
        return result;
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
        return ResolveAuthorizedHostPath(requestedPath, allowedDirectories, requiredAccess, out _, out _);
    }

    /// <summary>Resolves and authorizes a path and returns the roots needed for physical traversal.</summary>
    internal static string ResolveAuthorizedHostPath(
        string requestedPath,
        IReadOnlyList<AllowedDirectory> allowedDirectories,
        WorkspacePathAccess requiredAccess,
        out string traversalRoot,
        out string authorizedRoot)
    {
        ArgumentNullException.ThrowIfNull(allowedDirectories);

        traversalRoot = string.Empty;
        authorizedRoot = string.Empty;

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

        authorizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(containingDirectory.Value));

        traversalRoot = allowedDirectories
            .Where(directory => IsPathWithinDirectory(fullPath, directory.Value))
            .Select(directory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Value)))
            .OrderBy(static root => root.Length)
            .First();

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

        string currentPath = Path.TrimEndingDirectorySeparator(normalizedDirectoryPath);

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
