using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
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

    /// <summary>
    /// Creates search tools that resolve agent-facing paths against the active run's allowed directories.
    /// </summary>
    public SearchToolset(Func<IReadOnlyList<AllowedDirectory>>? workspaceResolver = null)
    {
        _workspaceResolver = workspaceResolver ?? (() => []);
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

        foreach (var filePath in EnumerateTargetFiles(searchPath, include))
        {
            if (IsBinaryFile(filePath))
            {
                continue;
            }

            string agentPath = PathHelper.ResolveToAgentPath(filePath, allowedDirectories);
            switch (mode)
            {
                case "files_with_matches":
                    if (FileHasMatches(filePath, regex))
                    {
                        results.Add(agentPath);
                    }
                    break;
                case "count":
                    var matchCount = CountMatches(filePath, regex);
                    if (matchCount > 0)
                    {
                        results.Add($"{agentPath}: {matchCount}");
                    }
                    break;
                case "content":
                    foreach (var entry in BuildContentResults(filePath, agentPath, regex, contextLines))
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

        if (!Directory.Exists(searchPath))
        {
            throw new ToolFailureException("The search directory was not found.", "Check the path and try again.");
        }

        var matcher = CreateMatcher();
        matcher.AddInclude(pattern);

        var matches = matcher.GetResultsInFullPath(searchPath)
            .Select(static fullPath => new FileInfo(fullPath))
            .Where(static file => file.Exists)
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ThenBy(static file => file.FullName, GetPathComparer())
            .Take(200)
            .Select(file => $"{PathHelper.ResolveToAgentPath(file.FullName, allowedDirectories)} ({file.Length} bytes)")
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

    // Resolve the agent-facing path to a host path so search honors the workspace, like the file tools.
    private static string ResolveSearchRoot(string? path, IReadOnlyList<AllowedDirectory> allowedDirectories)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return PathHelper.ResolveToHostPath(path, () => allowedDirectories);
        }

        var workingDirectory = PathHelper.ResolveWorkingDirectoryHostPath(allowedDirectories);
        return string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory;
    }

    private static bool FileHasMatches(string filePath, Regex regex)
    {
        foreach (var line in File.ReadLines(filePath))
        {
            if (regex.IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountMatches(string filePath, Regex regex)
    {
        var matchCount = 0;

        foreach (var line in File.ReadLines(filePath))
        {
            if (regex.IsMatch(line))
            {
                matchCount++;
            }
        }

        return matchCount;
    }

    private static IEnumerable<string> BuildContentResults(
        string filePath,
        string agentPath,
        Regex regex,
        int contextLines)
    {
        if (contextLines == 0)
        {
            var currentLineNumber = 0;
            foreach (var line in File.ReadLines(filePath))
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

        foreach (var line in File.ReadLines(filePath))
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

    private static IEnumerable<string> EnumerateTargetFiles(
        string searchPath,
        string? include)
    {
        if (File.Exists(searchPath))
        {
            yield return searchPath;
            yield break;
        }

        if (!Directory.Exists(searchPath))
        {
            throw new ToolFailureException("The search path was not found.", "Check the path and try again.");
        }

        if (string.IsNullOrWhiteSpace(include))
        {
            foreach (var file in Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories))
            {
                yield return file;
            }

            yield break;
        }

        var matcher = CreateMatcher();
        matcher.AddInclude(include);
        foreach (var file in matcher.GetResultsInFullPath(searchPath))
        {
            yield return file;
        }
    }

    private static Matcher CreateMatcher()
    {
        return OperatingSystem.IsWindows()
            ? new Matcher(StringComparison.OrdinalIgnoreCase)
            : new Matcher(StringComparison.Ordinal);
    }

    private static IComparer<string> GetPathComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private static bool IsBinaryFile(string filePath)
    {
        var buffer = new byte[8192];
        using var stream = File.OpenRead(filePath);
        var read = stream.Read(buffer, 0, buffer.Length);
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
