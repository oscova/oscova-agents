using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Oscova.Agents.Harness;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Toolsets;

/// <summary>
/// Searches file names and contents inside host-approved workspaces.
/// </summary>
public sealed class SearchToolset : ICrewToolset
{
    private readonly Func<IReadOnlyList<AllowedDirectory>> _workspaceResolver;
    private readonly IWorkspaceFileAccess _files;

    /// <summary>
    /// Creates search tools that resolve agent-facing paths against the active run's allowed directories.
    /// </summary>
    public SearchToolset(
        Func<IReadOnlyList<AllowedDirectory>>? workspaceResolver = null,
        IWorkspaceFileAccess? fileAccess = null)
    {
        _workspaceResolver = workspaceResolver ?? (() => []);
        _files = fileAccess ?? new WorkspaceFileAccess(_workspaceResolver);
    }

    /// <summary>
    /// Searches text files for a regular expression and returns matching files, counts, or content snippets.
    /// </summary>
    /// <param name="pattern">The regular expression pattern to search for.</param>
    /// <param name="path">The file or directory to search, or the working directory when omitted.</param>
    /// <param name="include">An optional glob filter, such as <c>*.cs</c>.</param>
    /// <param name="output_mode">The output mode: <c>files_with_matches</c>, <c>content</c>, or <c>count</c>.</param>
    /// <param name="context_lines">The number of surrounding lines to include for content output.</param>
    /// <param name="limit">The maximum number of results to return.</param>
    /// <returns>A newline-delimited search result, or a no-match message.</returns>
    [DisplayName("grep")]
    [Description("Searches file contents with a regex pattern.")]
    public string Grep(
        [Description("Regex pattern to search for")] string pattern,
        [Description("Directory or file to search in. Defaults to your working directory.")]
        [WorkspacePath(Access = WorkspacePathAccess.Read)]
        string? path = null,
        [Description("Optional glob filter such as *.cs")] string? include = null,
        [Description("files_with_matches, content, or count")] string output_mode = "files_with_matches",
        [Description("Context lines before and after matches when output_mode is content")] int context_lines = 0,
        [Description("Maximum number of results to return")] int limit = 100) =>
        Execute(() => GrepCore(pattern, path, include, output_mode, context_lines, limit));

    private string GrepCore(
        string pattern,
        string? path,
        string? include,
        string outputMode,
        int contextLines,
        int limit)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ToolFailureException("A search pattern is required.");
        }

        if (limit < 1)
        {
            limit = 1;
        }

        if (contextLines < 0)
        {
            contextLines = 0;
        }

        IReadOnlyList<AllowedDirectory> allowedDirectories = _workspaceResolver();
        var searchPath = ResolveSearchRoot(path, allowedDirectories);

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Multiline, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException)
        {
            throw new ToolFailureException("The regular expression is invalid.", "Correct the pattern and try again.");
        }
        var mode = outputMode?.Trim().ToLowerInvariant() ?? string.Empty;
        var results = new List<string>();

        foreach (WorkspaceFileEntry file in EnumerateTargetFiles(searchPath, include))
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            using Stream stream = _files.OpenRead(file.Path);
            if (IsBinaryFile(stream))
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            string agentPath = PathHelper.ResolveToAgentPath(file.Path, allowedDirectories);
            switch (mode)
            {
                case "files_with_matches":
                    if (FileHasMatches(reader, regex))
                    {
                        results.Add(agentPath);
                    }
                    break;
                case "count":
                    var matchCount = CountMatches(reader, regex);
                    if (matchCount > 0)
                    {
                        results.Add($"{agentPath}: {matchCount}");
                    }
                    break;
                case "content":
                    foreach (var entry in BuildContentResults(reader, agentPath, regex, contextLines))
                    {
                        results.Add(entry);
                        if (results.Count >= limit)
                        {
                            return string.Join(Environment.NewLine, results);
                        }
                    }
                    continue;
                default:
                    throw new ToolFailureException(
                        "The search output mode is invalid.",
                        "Use files_with_matches, content, or count.");
            }

            if (results.Count >= limit)
            {
                break;
            }
        }

        if (results.Count == 0)
        {
            return "No matches found.";
        }

        return string.Join(Environment.NewLine, results.Take(limit));
    }

    /// <summary>
    /// Finds files by glob pattern and returns recent matches first.
    /// </summary>
    /// <param name="pattern">The glob pattern to match, such as <c>**/*.cs</c>.</param>
    /// <param name="path">The directory to search, or the working directory when omitted.</param>
    /// <returns>A newline-delimited list of agent-facing paths and file sizes, or a no-match message.</returns>
    [DisplayName("glob")]
    [Description("Finds files by glob pattern and returns agent-facing paths sorted by last modified time.")]
    public string Glob(
        [Description("Glob pattern such as **/*.cs")] string pattern,
        [Description("Directory to search in. Defaults to your working directory.")]
        [WorkspacePath(Access = WorkspacePathAccess.Read)]
        string? path = null) => Execute(() => GlobCore(pattern, path));

    private string GlobCore(string pattern, string? path)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ToolFailureException("A glob pattern is required.");
        }

        IReadOnlyList<AllowedDirectory> allowedDirectories = _workspaceResolver();
        var searchPath = ResolveSearchRoot(path, allowedDirectories);

        WorkspaceFileEntry? root = _files.GetEntry(searchPath);
        if (root?.Attributes.HasFlag(FileAttributes.Directory) != true)
        {
            throw new ToolFailureException("The search directory was not found.", "Check the path and try again.");
        }

        var matches = MatchFiles(root.Path, pattern)
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ThenBy(static file => file.Path, GetPathComparer())
            .Take(200)
            .Select(file => $"{PathHelper.ResolveToAgentPath(file.Path, allowedDirectories)} ({file.Length} bytes)")
            .ToList();

        if (matches.Count == 0)
        {
            return "No files matched.";
        }

        return string.Join(Environment.NewLine, matches);
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
                "The search could not read the requested files.",
                "Check the path and workspace access, then try again.");
        }
    }

    private static string ResolveSearchRoot(string? path, IReadOnlyList<AllowedDirectory> allowedDirectories)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return PathHelper.ResolveAuthorizedHostPath(path, allowedDirectories, WorkspacePathAccess.Read);
        }

        var workingDirectory = PathHelper.ResolveWorkingDirectoryHostPath(allowedDirectories);
        return string.IsNullOrWhiteSpace(workingDirectory)
            ? throw new ToolFailureException(
                "No default workspace is available.",
                "Grant a readable workspace directory and try again.")
            : PathHelper.ResolveAuthorizedHostPath(workingDirectory, allowedDirectories, WorkspacePathAccess.Read);
    }

    private static bool FileHasMatches(TextReader reader, Regex regex)
    {
        while (reader.ReadLine() is { } line)
        {
            if (regex.IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountMatches(TextReader reader, Regex regex)
    {
        var matchCount = 0;

        while (reader.ReadLine() is { } line)
        {
            if (regex.IsMatch(line))
            {
                matchCount++;
            }
        }

        return matchCount;
    }

    private static IEnumerable<string> BuildContentResults(
        TextReader reader,
        string agentPath,
        Regex regex,
        int contextLines)
    {
        if (contextLines == 0)
        {
            var currentLineNumber = 0;
            while (reader.ReadLine() is { } line)
            {
                currentLineNumber++;
                if (regex.IsMatch(line))
                {
                    yield return $"{agentPath}:{currentLineNumber}: {line}";
                }
            }

            yield break;
        }

        var previousLines = new Queue<(int LineNumber, string Line)>();
        var activeBlock = new StringBuilder();
        var hasActiveBlock = false;
        var activeBlockEndLineNumber = 0;
        var lastAppendedLineNumber = 0;
        var lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var isMatch = regex.IsMatch(line);

            if (hasActiveBlock && lineNumber > activeBlockEndLineNumber && !isMatch)
            {
                activeBlock.Append("--");
                yield return activeBlock.ToString();
                activeBlock.Clear();
                hasActiveBlock = false;
                lastAppendedLineNumber = 0;
            }

            if (isMatch)
            {
                if (!hasActiveBlock)
                {
                    foreach (var previousContextLine in previousLines)
                    {
                        activeBlock.AppendLine($"{agentPath}:{previousContextLine.LineNumber}: {previousContextLine.Line}");
                    }

                    hasActiveBlock = true;
                    lastAppendedLineNumber = previousLines.Count > 0
                        ? previousLines.Last().LineNumber
                        : 0;
                }

                if (lineNumber > lastAppendedLineNumber)
                {
                    activeBlock.AppendLine($"{agentPath}:{lineNumber}: {line}");
                    lastAppendedLineNumber = lineNumber;
                }

                activeBlockEndLineNumber = lineNumber + contextLines;
            }
            else if (hasActiveBlock && lineNumber <= activeBlockEndLineNumber)
            {
                activeBlock.AppendLine($"{agentPath}:{lineNumber}: {line}");
                lastAppendedLineNumber = lineNumber;
            }

            previousLines.Enqueue((lineNumber, line));
            while (previousLines.Count > contextLines)
            {
                previousLines.Dequeue();
            }
        }

        if (hasActiveBlock)
        {
            activeBlock.Append("--");
            yield return activeBlock.ToString();
        }
    }

    private IEnumerable<WorkspaceFileEntry> EnumerateTargetFiles(
        string searchPath,
        string? include)
    {
        WorkspaceFileEntry? root = _files.GetEntry(searchPath);
        if (root is null)
        {
            throw new ToolFailureException("The search path was not found.", "Check the path and try again.");
        }

        if (!root.Attributes.HasFlag(FileAttributes.Directory))
        {
            yield return root;
            yield break;
        }

        IEnumerable<WorkspaceFileEntry> files = EnumerateFiles(root.Path);
        if (string.IsNullOrWhiteSpace(include))
        {
            foreach (WorkspaceFileEntry file in files)
            {
                yield return file;
            }

            yield break;
        }

        foreach (WorkspaceFileEntry file in MatchFiles(root.Path, include))
        {
            yield return file;
        }
    }

    private IEnumerable<WorkspaceFileEntry> MatchFiles(string directory, string pattern)
    {
        var matcher = CreateMatcher();
        matcher.AddInclude(pattern);
        var entries = new Dictionary<string, WorkspaceFileEntry>(GetPathComparer());
        // Let the matcher prune excluded subtrees before any contained directory enumeration.
        foreach (FilePatternMatch match in matcher.Execute(new WorkspaceDirectory(_files, directory, entries)).Files)
        {
            string path = Path.GetFullPath(Path.Combine(directory, PathHelper.ToPlatformRelativePath(match.Path)));
            yield return entries[path];
        }
    }

    /// <summary>Lets the glob matcher traverse directories through the workspace boundary.</summary>
    private sealed class WorkspaceDirectory(
        IWorkspaceFileAccess files, string path, Dictionary<string, WorkspaceFileEntry> entries, bool isParentPath = false) : DirectoryInfoBase
    {
        public override string FullName => path;
        public override string Name => isParentPath ? ".." : Path.GetFileName(path);
        public override DirectoryInfoBase? ParentDirectory => Path.GetDirectoryName(path) is { } parent
            ? new WorkspaceDirectory(files, parent, entries)
            : null;

        public override DirectoryInfoBase GetDirectory(string name) =>
            new WorkspaceDirectory(files, Path.GetFullPath(Path.Combine(path, name)), entries, name == "..");

        public override FileInfoBase GetFile(string name) =>
            new WorkspaceFile(Path.GetFullPath(Path.Combine(path, name)), this);

        public override IEnumerable<FileSystemInfoBase> EnumerateFileSystemInfos()
        {
            foreach (WorkspaceFileEntry entry in files.Enumerate(path))
            {
                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        yield return new WorkspaceDirectory(files, entry.Path, entries);
                    }
                }
                else
                {
                    // Reuse no-follow metadata so glob can report link names without opening their targets.
                    entries[entry.Path] = entry;
                    yield return new WorkspaceFile(entry.Path, this);
                }
            }
        }
    }

    /// <summary>Supplies logical file names to the glob matcher without reopening host paths.</summary>
    private sealed class WorkspaceFile(string path, DirectoryInfoBase parent) : FileInfoBase
    {
        public override string FullName => path;
        public override string Name => Path.GetFileName(path);
        public override DirectoryInfoBase ParentDirectory => parent;
    }

    private IEnumerable<WorkspaceFileEntry> EnumerateFiles(string directory)
    {
        foreach (WorkspaceFileEntry entry in _files.Enumerate(directory))
        {
            if (entry.Attributes.HasFlag(FileAttributes.Directory))
            {
                if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    foreach (WorkspaceFileEntry child in EnumerateFiles(entry.Path)) yield return child;
                }

                continue;
            }

            yield return entry;
        }
    }

    private static Matcher CreateMatcher()
    {
        return OperatingSystem.IsWindows()
            ? new Matcher(StringComparison.OrdinalIgnoreCase)
            : new Matcher(StringComparison.Ordinal);
    }

    private static StringComparer GetPathComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private static bool IsBinaryFile(Stream stream)
    {
        var buffer = new byte[8192];
        var read = stream.Read(buffer, 0, buffer.Length);
        stream.Position = 0;
        for (var index = 0; index < read; index++)
        {
            if (buffer[index] == 0)
            {
                return true;
            }
        }

        return false;
    }
}
