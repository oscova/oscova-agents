using System;
using System.Collections.Generic;
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
/// Shares bounded text-file operations across concrete storage-backed toolsets.
/// </summary>
public abstract class TextFileToolsetBase
{
    private const int MaxLineLength = 2000;
    private const int PageCharBudget = 50000;
    private const int GlobResultLimit = 200;

    /// <summary>Resolves directories granted to the active run.</summary>
    protected Func<IReadOnlyList<AllowedDirectory>> WorkspaceResolver { get; }
    /// <summary>Reads files through workspace access checks.</summary>
    protected IWorkspaceFileAccess Files { get; }
    /// <summary>Saves search results that exceed the inline result limit.</summary>
    protected IToolOutputStore? OutputStore { get; }

    /// <summary>Shares workspace access across file and search operations.</summary>
    protected TextFileToolsetBase(Func<IReadOnlyList<AllowedDirectory>>? workspaceResolver, IWorkspaceFileAccess? fileAccess, IToolOutputStore? outputStore)
    {
        WorkspaceResolver = workspaceResolver ?? (() => []);
        Files = fileAccess ?? new WorkspaceFileAccess(WorkspaceResolver);
        OutputStore = outputStore;
    }

    /// <summary>Reports whether an enumerated file belongs in search results.</summary>
    protected virtual bool IsSearchable(string hostPath) => true;

    /// <summary>Opens searchable text and skips binary files.</summary>
    protected virtual TextReader? OpenSearchText(string hostPath)
    {
        Stream stream = Files.OpenRead(hostPath);
        if (IsBinaryFile(stream))
        {
            stream.Dispose();
            return null;
        }

        return new StreamReader(stream);
    }

    /// <summary>Reads a bounded page from a storage-backed text file.</summary>
    protected string ReadFileCore(string path, int offset, int limit)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ToolFailureException("A file path is required.");
        }
        offset = Math.Max(1, offset);
        limit = Math.Max(1, limit);

        using var reader = OpenText(path, out var sizeBytes);
        path = DisplayRequestedPath(path);
        var builder = new StringBuilder();
        var currentLineNumber = 0;
        var capturedLineCount = 0;
        var lastLineNumber = offset - 1;
        var pageChars = 0;
        var pageStoppedEarly = false;

        while (reader.ReadLine() is { } line)
        {
            currentLineNumber++;
            if (currentLineNumber < offset || capturedLineCount >= limit || pageStoppedEarly)
            {
                continue;
            }

            var entry = $"{currentLineNumber,4} | {TruncateLine(line)}";
            if (capturedLineCount > 0 && pageChars + entry.Length > PageCharBudget)
            {
                pageStoppedEarly = true;
                continue;
            }

            builder.AppendLine(entry);
            pageChars += entry.Length + 1;
            capturedLineCount++;
            lastLineNumber = currentLineNumber;
        }

        if (currentLineNumber < offset)
        {
            return $"File: {path} | Total lines: {currentLineNumber} ({sizeBytes} bytes)\nOffset {offset} is beyond the end of the file.";
        }

        var header = $"File: {path} | Lines {offset}-{lastLineNumber} of {currentLineNumber} ({sizeBytes} bytes)";
        if (capturedLineCount == 0)
        {
            return $"{header}\nNo lines to display.";
        }

        var body = builder.ToString().TrimEnd();
        if (lastLineNumber < currentLineNumber)
        {
            var reason = pageStoppedEarly
                ? $"Page stopped at the {PageCharBudget}-character budget."
                : $"Reached the {limit}-line page limit.";
            body += $"\n[{reason} Continue with offset={lastLineNumber + 1} of {currentLineNumber}.]";
        }

        return $"{header}\n{body}";
    }

    /// <summary>Writes or appends text through the concrete storage implementation.</summary>
    protected string WriteFileCore(string path, string? content, bool append)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        content ??= string.Empty;
        WriteText(path, content, append);
        path = DisplayRequestedPath(path);
        return append
            ? $"Appended {content.Length} characters to '{path}'."
            : $"Wrote {content.Length} characters to '{path}'.";
    }

    /// <summary>Replaces exact text through the concrete storage implementation.</summary>
    protected string EditFileCore(string path, string oldValue, string newValue, bool replaceAll)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(oldValue);
        ArgumentNullException.ThrowIfNull(newValue);

        string content;
        using (var reader = OpenText(path, out _))
        {
            content = reader.ReadToEnd();
        }
        var occurrenceCount = CountOccurrences(content, oldValue);
        // Models send LF text, so an unmatched edit retries in a CRLF file's own line endings.
        if (occurrenceCount == 0 && DetectNewLine(content) == "\r\n")
        {
            oldValue = ToNewLine(oldValue, "\r\n");
            newValue = ToNewLine(newValue, "\r\n");
            occurrenceCount = CountOccurrences(content, oldValue);
        }
        // Models often add or drop trailing spaces, so whole lines are retried ignoring them only when nothing matched exactly.
        // Indentation stays exact because it is meaningful in Python and YAML. Only the one matched span is replaced.
        if (occurrenceCount == 0 && FindTrailingWhitespaceSpan(content, oldValue) is { } span)
        {
            WriteText(path, string.Concat(content[..span.Start], newValue, content[span.End..]), append: false);
            return $"Replaced 1 occurrence(s) in '{DisplayRequestedPath(path)}'.";
        }
        if (occurrenceCount == 0)
        {
            throw new ToolFailureException(
                "The text to replace was not found.",
                "Read the file again and copy the exact current text, without the line-number prefix.");
        }

        if (!replaceAll && occurrenceCount > 1)
        {
            throw new ToolFailureException(
                "The text to replace is not unique.",
                "Use replace_all or provide more context and try again.");
        }

        var replacementCount = replaceAll ? occurrenceCount : 1;
        var updated = replaceAll
            ? content.Replace(oldValue, newValue, StringComparison.Ordinal)
            : ReplaceFirst(content, oldValue, newValue);
        WriteText(path, updated, append: false);
        return $"Replaced {replacementCount} occurrence(s) in '{DisplayRequestedPath(path)}'.";
    }

    /// <summary>Returns the file's existing line ending, or the platform default when it has none.</summary>
    private static string DetectNewLine(string content)
        => content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : content.Contains('\n', StringComparison.Ordinal) ? "\n"
            : Environment.NewLine;

    private static string ToNewLine(string text, string newLine)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newLine, StringComparison.Ordinal);

    private string DisplayRequestedPath(string path)
    {
        var directories = WorkspaceResolver();
        string hostPath = PathHelper.ResolveAuthorizedHostPath(path, directories, WorkspacePathAccess.Read);
        return PathHelper.ResolveToAgentPath(hostPath, directories);
    }

    /// <summary>Applies the supported structured patch through the concrete storage implementation.</summary>
    protected string ApplyPatchCore(string patch, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        PatchDocument document;
        try
        {
            document = PatchParser.Parse(patch);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new ToolFailureException("The patch format is invalid.", PatchParser.FormatCorrection);
        }
        var added = 0;
        var updated = 0;
        var deleted = 0;
        var moved = 0;

        foreach (var operation in document.Operations)
        {
            switch (operation)
            {
                case AddFileOperation addFile:
                    WriteText(
                        ResolvePatchPath(addFile.Path, baseDirectory),
                        addFile.Content.Replace("\n", Environment.NewLine, StringComparison.Ordinal),
                        append: false);
                    added++;
                    break;
                case DeleteFileOperation deleteFile:
                    DeleteText(ResolvePatchPath(deleteFile.Path, baseDirectory));
                    deleted++;
                    break;
                case UpdateFileOperation updateFile:
                    ApplyUpdateFile(updateFile, baseDirectory, ref moved);
                    updated++;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported patch operation: {operation.GetType().Name}");
            }
        }

        return $"Updated {updated} file(s), added {added} file(s), deleted {deleted} file(s), moved {moved} file(s).";
    }

    /// <summary>Opens one text file and reports its plaintext byte size.</summary>
    protected abstract TextReader OpenText(string path, out long sizeBytes);

    /// <summary>Writes or appends text to one file.</summary>
    protected abstract void WriteText(string path, string content, bool append);

    /// <summary>Deletes one file when it exists.</summary>
    protected abstract void DeleteText(string path);

    /// <summary>Publishes patched content, optionally moving the logical file.</summary>
    protected abstract void MoveText(string sourcePath, string targetPath, string content, bool contentChanged);

    /// <summary>Resolves a path embedded in a patch against its base directory.</summary>
    protected abstract string ResolvePatchPath(string path, string baseDirectory);

    /// <summary>Resolves a patch path through the standard workspace containment contract.</summary>
    protected static string ResolveWorkspacePatchPath(
        string path,
        string baseDirectory,
        Func<IReadOnlyList<AllowedDirectory>> workspaceResolver)
    {
        var normalizedBaseDirectory = Path.GetFullPath(PathHelper.ResolveToHostPath(baseDirectory, workspaceResolver));
        var resolvedPath = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(normalizedBaseDirectory, PathHelper.ToPlatformRelativePath(path)));

        if (!PathHelper.IsPathWithinDirectory(resolvedPath, normalizedBaseDirectory))
        {
            throw new ToolFailureException(
                "The patch path is outside its base directory.",
                "Use a path inside the selected base directory.");
        }

        try
        {
            // Patch paths are inside patch text, so invocation middleware cannot authorize them.
            return PathHelper.ResolveAuthorizedHostPath(
                resolvedPath,
                workspaceResolver(),
                WorkspacePathAccess.Write);
        }
        catch (UnauthorizedAccessException)
        {
            throw new ToolFailureException(
                "The patch path is not within a writable granted folder.",
                "Use a path within a writable granted folder.");
        }
    }

    private void ApplyUpdateFile(UpdateFileOperation operation, string baseDirectory, ref int moved)
    {
        var sourcePath = ResolvePatchPath(operation.Path, baseDirectory);
        string content;
        string newLine;
        using (var reader = OpenText(sourcePath, out _))
        {
            var original = reader.ReadToEnd();
            // The rewrite keeps this file's own line endings instead of the host platform's.
            newLine = DetectNewLine(original);
            content = original.Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        var hasTrailingNewline = content.EndsWith('\n');
        var lines = content.Length == 0 ? [] : content.Split('\n').ToList();
        if (hasTrailingNewline && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var searchStartIndex = 0;
        foreach (var hunk in operation.Hunks)
        {
            ApplyHunk(lines, hunk, ref searchStartIndex);
        }

        var updatedContent = string.Join('\n', lines);
        if (hasTrailingNewline || operation.Hunks.Any(static h => h.IsEndOfFile))
        {
            updatedContent += "\n";
        }

        var targetPath = operation.MoveTo is null
            ? sourcePath
            : ResolvePatchPath(operation.MoveTo, baseDirectory);
        MoveText(
            sourcePath,
            targetPath,
            ToNewLine(updatedContent, newLine),
            operation.Hunks.Count > 0);

        if (!string.Equals(sourcePath, targetPath, StringComparison.Ordinal))
        {
            moved++;
        }
    }

    private static void ApplyHunk(List<string> lines, PatchHunk hunk, ref int searchStartIndex)
    {
        var oldLines = hunk.Lines
            .Where(static line => line.Prefix is ' ' or '-')
            .Select(static line => line.Text)
            .ToList();
        var newLines = hunk.Lines
            .Where(static line => line.Prefix is ' ' or '+')
            .Select(static line => line.Text)
            .ToList();
        var matchIndex = FindUniqueMatchIndex(lines, oldLines, searchStartIndex, hunk.Header, ignoreTrailingWhitespace: false);
        // Same tolerance as edit: trailing spaces are ignored only when no exact match exists, indentation stays exact.
        if (matchIndex < 0)
        {
            matchIndex = FindUniqueMatchIndex(lines, oldLines, searchStartIndex, hunk.Header, ignoreTrailingWhitespace: true);
        }

        if (matchIndex < 0)
        {
            throw new ToolFailureException(
                "A patch section could not be located.",
                "Refresh the file content and try again.");
        }

        lines.RemoveRange(matchIndex, oldLines.Count);
        lines.InsertRange(matchIndex, newLines);
        searchStartIndex = matchIndex + newLines.Count;
    }

    private static int FindUniqueMatchIndex(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> target,
        int startIndex,
        string? header,
        bool ignoreTrailingWhitespace)
    {
        var normalizedStartIndex = Math.Max(0, startIndex);
        // An "@@ text" header names a line above the hunk, so the first match below it wins.
        if (header is not null)
        {
            for (var index = normalizedStartIndex; index < lines.Count; index++)
            {
                if (lines[index].Contains(header, StringComparison.Ordinal))
                {
                    var belowHeader = FindMatchIndexes(lines, target, index + 1, ignoreTrailingWhitespace);
                    // Ignoring trailing spaces is only safe when it points to a single location.
                    if (ignoreTrailingWhitespace && belowHeader.Count > 1)
                    {
                        throw new ToolFailureException(
                            "A patch section matched multiple locations.",
                            "Add more surrounding context and try again.");
                    }

                    if (belowHeader.Count > 0)
                    {
                        return belowHeader[0];
                    }

                    break;
                }
            }
        }

        var matches = FindMatchIndexes(lines, target, normalizedStartIndex, ignoreTrailingWhitespace);
        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            throw new ToolFailureException(
                "A patch section matched multiple locations.",
                "Add more surrounding context and try again.");
        }

        if (normalizedStartIndex == 0)
        {
            return -1;
        }

        matches = FindMatchIndexes(lines, target, 0, ignoreTrailingWhitespace);
        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            throw new ToolFailureException(
                "A patch section matched multiple locations.",
                "Add more surrounding context and try again.");
        }

        return -1;
    }

    /// <summary>Returns the range of the one whole-line span that equals the value apart from trailing whitespace.</summary>
    private static Range? FindTrailingWhitespaceSpan(string content, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var endsWithNewLine = value.EndsWith('\n');
        var target = (endsWithNewLine ? value[..^1] : value).Split('\n');
        var lines = content.Split('\n');
        var matches = FindMatchIndexes(lines, target, 0, ignoreTrailingWhitespace: true);
        if (matches.Count != 1)
        {
            return null;
        }

        var start = lines.Take(matches[0]).Sum(line => line.Length + 1);
        var end = start + lines.Skip(matches[0]).Take(target.Length).Sum(line => line.Length + 1) - 1;
        // In a CRLF file each split line keeps its '\r', which belongs to the line break after the span.
        if (endsWithNewLine)
        {
            end = Math.Min(end + 1, content.Length);
        }
        else if (content[end - 1] == '\r')
        {
            end--;
        }

        return start..end;
    }

    private static List<int> FindMatchIndexes(IReadOnlyList<string> lines, IReadOnlyList<string> target, int startIndex, bool ignoreTrailingWhitespace)
    {
        if (target.Count == 0)
        {
            return [Math.Clamp(startIndex, 0, lines.Count)];
        }

        var indexes = new List<int>();
        for (var index = Math.Max(0, startIndex); index <= lines.Count - target.Count; index++)
        {
            var matched = true;
            for (var offset = 0; offset < target.Count; offset++)
            {
                var equal = ignoreTrailingWhitespace
                    ? lines[index + offset].AsSpan().TrimEnd().SequenceEqual(target[offset].AsSpan().TrimEnd())
                    : string.Equals(lines[index + offset], target[offset], StringComparison.Ordinal);
                if (!equal)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                indexes.Add(index);
            }
        }

        return indexes;
    }

    private static string TruncateLine(string line)
    {
        if (line.Length <= MaxLineLength)
        {
            return line;
        }

        return $"{line[..MaxLineLength]} ... [+{line.Length - MaxLineLength} chars truncated]";
    }

    private static string ReplaceFirst(string content, string oldValue, string newValue)
    {
        var index = content.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0
            ? content
            : string.Concat(content.AsSpan(0, index), newValue, content.AsSpan(index + oldValue.Length));
    }

    private static int CountOccurrences(string content, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var count = 0;
        var startIndex = 0;
        while ((startIndex = content.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
    }

    /// <summary>Searches readable text in the active workspace.</summary>
    protected string GrepCore(
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

        IReadOnlyList<AllowedDirectory> allowedDirectories = WorkspaceResolver();
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
        var results = new ToolOutputResults(limit);

        foreach (WorkspaceFileEntry file in EnumerateTargetFiles(searchPath, include))
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (!IsSearchable(file.Path))
            {
                continue;
            }

            using TextReader? reader = OpenSearchText(file.Path);
            if (reader is null)
            {
                continue;
            }
            string agentPath = PathHelper.ResolveToAgentPath(file.Path, allowedDirectories);
            switch (mode)
            {
                case "files_with_matches":
                    if (FileHasMatches(reader, regex))
                    {
                        results.Record(agentPath);
                    }
                    break;
                case "count":
                    var matchCount = CountMatches(reader, regex);
                    if (matchCount > 0)
                    {
                        results.Record($"{agentPath}: {matchCount}");
                    }
                    break;
                case "content":
                    foreach (var entry in BuildContentResults(reader, agentPath, regex, contextLines))
                    {
                        results.Record(entry);
                    }
                    continue;
                default:
                    throw new ToolFailureException(
                        "The search output mode is invalid.",
                        "Use files_with_matches, content, or count.");
            }

        }

        if (results.Total == 0)
        {
            return "No matches found.";
        }

        return results.Compose(OutputStore, "grep");
    }

    /// <summary>Finds workspace files matching a glob pattern.</summary>
    protected string GlobCore(string pattern, string? path)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ToolFailureException("A glob pattern is required.");
        }

        IReadOnlyList<AllowedDirectory> allowedDirectories = WorkspaceResolver();
        var searchPath = ResolveSearchRoot(path, allowedDirectories);

        WorkspaceFileEntry? root = Files.GetEntry(searchPath);
        if (root?.Attributes.HasFlag(FileAttributes.Directory) != true)
        {
            throw new ToolFailureException("The search directory was not found.", "Check the path and try again.");
        }

        var results = new ToolOutputResults(GlobResultLimit);
        foreach (WorkspaceFileEntry file in MatchFiles(root.Path, pattern)
            .Where(file => IsSearchable(file.Path))
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ThenBy(static file => file.Path, GetPathComparer()))
        {
            results.Record($"{PathHelper.ResolveToAgentPath(file.Path, allowedDirectories)} ({file.Length} bytes)");
        }

        if (results.Total == 0)
        {
            return $"No files matched under {PathHelper.ResolveToAgentPath(root.Path, allowedDirectories)}.";
        }

        return results.Compose(OutputStore, "glob");
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
        WorkspaceFileEntry? root = Files.GetEntry(searchPath);
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
        foreach (FilePatternMatch match in matcher.Execute(new WorkspaceDirectory(Files, directory, entries)).Files)
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
        foreach (WorkspaceFileEntry entry in Files.Enumerate(directory))
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
