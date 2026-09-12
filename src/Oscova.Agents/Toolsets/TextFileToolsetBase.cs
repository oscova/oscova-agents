using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        if (occurrenceCount == 0)
        {
            throw new ToolFailureException(
                "The text to replace was not found.",
                "Check the text or provide more context and try again.");
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
        return $"Replaced {replacementCount} occurrence(s) in '{path}'.";
    }

    /// <summary>Applies the supported structured patch through the concrete storage implementation.</summary>
    protected string ApplyPatchCore(string patch, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var document = PatchParser.Parse(patch);
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

        var allowedDirectories = workspaceResolver();
        if (allowedDirectories.Count == 0)
        {
            return resolvedPath;
        }

        if (!PathHelper.TryGetContainingDirectory(resolvedPath, allowedDirectories, out var containingDirectory) ||
            containingDirectory is null)
        {
            throw new ToolFailureException(
                "The patch path is outside the allowed workspace.",
                "Use a path from Workspace Access.");
        }

        if (PathHelper.HasSymlinkInPath(resolvedPath, containingDirectory.Value))
        {
            throw new ToolFailureException(
                "The patch path contains a link that cannot be followed.",
                "Use a direct path inside the workspace.");
        }

        if (containingDirectory.AccessMode != WorkspaceAccessMode.ReadWrite)
        {
            throw new ToolFailureException(
                "The patch path is read-only.",
                "Choose a writable workspace path.");
        }

        return resolvedPath;
    }

    private void ApplyUpdateFile(UpdateFileOperation operation, string baseDirectory, ref int moved)
    {
        var sourcePath = ResolvePatchPath(operation.Path, baseDirectory);
        string content;
        using (var reader = OpenText(sourcePath, out _))
        {
            content = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
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
            updatedContent.Replace("\n", Environment.NewLine, StringComparison.Ordinal),
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
        var matchIndex = FindUniqueMatchIndex(lines, oldLines, searchStartIndex, hunk.Header);

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
        string? header)
    {
        var normalizedStartIndex = Math.Max(0, startIndex);
        var matches = FindMatchIndexes(lines, target, normalizedStartIndex);
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

        matches = FindMatchIndexes(lines, target, 0);
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

    private static List<int> FindMatchIndexes(IReadOnlyList<string> lines, IReadOnlyList<string> target, int startIndex)
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
                if (!string.Equals(lines[index + offset], target[offset], StringComparison.Ordinal))
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
}
