using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Oscova.Agents.Harness;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Toolsets;

/// <summary>
/// Lists files and directories inside host-approved workspaces.
/// </summary>
public sealed class DirectoryToolset : ICrewToolset
{
    private readonly Func<IReadOnlyList<AllowedDirectory>> _workspaceResolver;

    /// <summary>
    /// Creates directory tools that resolve agent-facing paths against the active run's allowed directories.
    /// </summary>
    public DirectoryToolset(Func<IReadOnlyList<AllowedDirectory>>? workspaceResolver = null)
    {
        _workspaceResolver = workspaceResolver ?? (() => []);
    }

    /// <summary>
    /// Lists directory entries with depth control and pagination.
    /// </summary>
    /// <param name="path">The relative, agent-facing, or host directory path the active workspace allows reading.</param>
    /// <param name="offset">The one-based entry index where the page starts.</param>
    /// <param name="limit">The maximum number of entries to return.</param>
    /// <param name="depth">The maximum child-directory depth to traverse.</param>
    /// <returns>A formatted directory listing with range metadata.</returns>
    [DisplayName("list_directory")]
    [Description("Lists files and folders with depth control and pagination.")]
    public string ListDirectory(
        [Description("Directory path: relative to your working directory, or an absolute path listed under Workspace Access")]
        [WorkspacePath(Access = WorkspacePathAccess.Read)]
        string path,
        [Description("1-indexed entry number to start from")] int offset = 1,
        [Description("Maximum number of entries to return")] int limit = 25,
        [Description("Maximum directory depth to traverse")] int depth = 2) =>
        Execute(() => ListDirectoryCore(path, offset, limit, depth));

    private string ListDirectoryCore(string path, int offset, int limit, int depth)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ToolFailureException("A directory path is required.");
        }

        string hostPath = PathHelper.ResolveToHostPath(path, _workspaceResolver);

        if (!Directory.Exists(hostPath))
        {
            throw new ToolFailureException("The requested directory was not found.", "Check the path and try again.");
        }

        if (offset < 1)
        {
            offset = 1;
        }

        if (limit < 1)
        {
            limit = 1;
        }

        if (depth < 0)
        {
            depth = 0;
        }

        var entries = new List<string>();
        AppendEntries(entries, hostPath, 0, depth);

        if (entries.Count < offset)
        {
            return $"Directory: {path} | Total entries: {entries.Count} | Depth: {depth}\nOffset {offset} is beyond the end of the listing.";
        }

        var page = entries.Skip(offset - 1).Take(limit).ToList();
        var lastEntry = offset + page.Count - 1;
        var header = $"Directory: {path} | Entries {offset}-{lastEntry} of {entries.Count} | Depth: {depth}";
        return $"{header}\n{string.Join(Environment.NewLine, page)}";
    }

    private static string Execute(Func<string> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new ToolFailureException(
                "The directory could not be read.",
                "Check the path and workspace access, then try again.");
        }
    }

    private static void AppendEntries(List<string> entries, string directoryPath, int currentDepth, int maxDepth)
    {
        if (currentDepth > maxDepth)
        {
            return;
        }

        var indent = new string(' ', currentDepth * 2);
        var directoryInfo = new DirectoryInfo(directoryPath);

        var directories = directoryInfo
            .EnumerateDirectories()
            .OrderBy(static directory => directory.Name, GetComparer())
            .ToList();

        foreach (var directory in directories)
        {
            entries.Add($"{indent}[DIR] {directory.Name}");
            // List links themselves without letting recursion escape the approved root.
            if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                AppendEntries(entries, directory.FullName, currentDepth + 1, maxDepth);
            }
        }

        var files = directoryInfo
            .EnumerateFiles()
            .OrderBy(static file => file.Name, GetComparer());

        foreach (var file in files)
        {
            entries.Add($"{indent}[FILE] {file.Name} ({file.Length} bytes)");
        }
    }

    private static StringComparer GetComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }
}
