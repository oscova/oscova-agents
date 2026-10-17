using System;
using System.Collections.Generic;
using System.Text;

namespace Oscova.Agents.Toolsets;

/// <summary>
/// Parses the structured patch format accepted by the coding toolset.
/// </summary>
public static class PatchParser
{
    internal const string FormatDescription = """
        Applies a structured multi-file patch relative to base_directory.
        Start the patch with *** Begin Patch.
        End the patch with *** End Patch.
        To update a file, use:
        *** Update File: path
        @@
        -old line
        +new line
        To create a file, use *** Add File: path followed by one or more + lines.
        To delete a file, use *** Delete File: path.
        Prefix each update hunk line with a space, +, or -.
        Keep every patch path inside base_directory.
        """;

    internal const string FormatCorrection = """
        Start with *** Begin Patch.
        Use *** Add File: path, *** Update File: path, or *** Delete File: path.
        For updates, add @@ and prefix each hunk line with a space, +, or -.
        End with *** End Patch.
        """;

    /// <summary>
    /// Parses a complete patch document into file operations.
    /// </summary>
    /// <param name="patch">The patch text, optionally wrapped in a heredoc.</param>
    /// <returns>A patch document containing ordered file operations.</returns>
    public static PatchDocument Parse(string patch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patch);

        var normalized = NormalizePatch(patch);
        var lines = normalized.Split('\n');
        var index = 0;

        RequireLine(lines, ref index, "*** Begin Patch");

        var operations = new List<PatchOperation>();

        while (index < lines.Length)
        {
            var line = lines[index];
            if (line == "*** End Patch")
            {
                index++;
                SkipTrailingEmptyLines(lines, ref index);
                if (index != lines.Length)
                {
                    throw new InvalidOperationException("Unexpected content after *** End Patch.");
                }

                return new PatchDocument(operations);
            }

            if (line.StartsWith("*** Add File: ", StringComparison.Ordinal))
            {
                operations.Add(ParseAddFile(lines, ref index));
                continue;
            }

            if (line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                operations.Add(ParseDeleteFile(lines, ref index));
                continue;
            }

            if (line.StartsWith("*** Update File: ", StringComparison.Ordinal))
            {
                operations.Add(ParseUpdateFile(lines, ref index));
                continue;
            }

            throw new InvalidOperationException($"Unexpected patch line: {line}");
        }

        throw new InvalidOperationException("Patch is missing *** End Patch.");
    }

    private static AddFileOperation ParseAddFile(string[] lines, ref int index)
    {
        var path = ReadHeaderValue(lines[index], "*** Add File: ");
        index++;

        var contentLines = new List<string>();
        while (index < lines.Length && !IsPatchHeader(lines[index]))
        {
            var line = lines[index];
            if (!line.StartsWith('+'))
            {
                throw new InvalidOperationException($"Invalid add file line: {line}");
            }

            contentLines.Add(line[1..]);
            index++;
        }

        if (contentLines.Count == 0)
        {
            throw new InvalidOperationException($"Add file operation for '{path}' has no content.");
        }

        return new AddFileOperation(path, string.Join('\n', contentLines));
    }

    private static DeleteFileOperation ParseDeleteFile(string[] lines, ref int index)
    {
        var path = ReadHeaderValue(lines[index], "*** Delete File: ");
        index++;
        return new DeleteFileOperation(path);
    }

    private static UpdateFileOperation ParseUpdateFile(string[] lines, ref int index)
    {
        var path = ReadHeaderValue(lines[index], "*** Update File: ");
        index++;

        string? moveTo = null;
        if (index < lines.Length && lines[index].StartsWith("*** Move to: ", StringComparison.Ordinal))
        {
            moveTo = ReadHeaderValue(lines[index], "*** Move to: ");
            index++;
        }

        var hunks = new List<PatchHunk>();
        while (index < lines.Length && !IsPatchHeader(lines[index]))
        {
            if (!lines[index].StartsWith("@@", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected hunk header, found: {lines[index]}");
            }

            hunks.Add(ParseHunk(lines, ref index));
        }

        return new UpdateFileOperation(path, moveTo, hunks);
    }

    private static PatchHunk ParseHunk(string[] lines, ref int index)
    {
        var headerLine = lines[index];
        string? header = null;
        if (headerLine.Length > 2)
        {
            header = headerLine[2..].Trim();
            if (string.IsNullOrWhiteSpace(header))
            {
                header = null;
            }
        }

        index++;

        var linesInHunk = new List<PatchHunkLine>();
        while (index < lines.Length && !IsPatchBoundary(lines[index]))
        {
            var line = lines[index];
            if (line == "*** End of File")
            {
                index++;
                return new PatchHunk(header, linesInHunk, true);
            }

            if (line.Length == 0)
            {
                throw new InvalidOperationException("Patch hunk lines must include a prefix character.");
            }

            var prefix = line[0];
            if (prefix != ' ' && prefix != '+' && prefix != '-')
            {
                throw new InvalidOperationException($"Invalid hunk line prefix '{prefix}'.");
            }

            linesInHunk.Add(new PatchHunkLine(prefix, line[1..]));
            index++;
        }

        return new PatchHunk(header, linesInHunk, false);
    }

    private static bool IsPatchBoundary(string line)
    {
        return line.StartsWith("@@", StringComparison.Ordinal) || IsPatchHeader(line);
    }

    private static bool IsPatchHeader(string line)
    {
        return line == "*** End Patch"
            || line.StartsWith("*** Add File: ", StringComparison.Ordinal)
            || line.StartsWith("*** Delete File: ", StringComparison.Ordinal)
            || line.StartsWith("*** Update File: ", StringComparison.Ordinal);
    }

    private static void RequireLine(string[] lines, ref int index, string expected)
    {
        if (index >= lines.Length || !string.Equals(lines[index], expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{expected}'.");
        }

        index++;
    }

    private static void SkipTrailingEmptyLines(string[] lines, ref int index)
    {
        while (index < lines.Length && string.IsNullOrEmpty(lines[index]))
        {
            index++;
        }
    }

    private static string ReadHeaderValue(string line, string prefix)
    {
        var value = line[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Patch header '{prefix}' is missing a path.");
        }

        return value;
    }

    private static string NormalizePatch(string patch)
    {
        var normalized = patch.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var lines = normalized.Split('\n');
        if (lines.Length >= 3 && IsHeredocStart(lines[0]))
        {
            var terminator = lines[0].Split("<<", StringSplitOptions.None)[1].Trim().Trim('\'', '"');
            var endIndex = Array.FindIndex(lines, 1, line => string.Equals(line.Trim(), terminator, StringComparison.Ordinal));
            if (endIndex > 0)
            {
                var builder = new StringBuilder();
                for (var i = 1; i < endIndex; i++)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append('\n');
                    }

                    builder.Append(lines[i]);
                }

                return builder.ToString();
            }
        }

        return normalized;
    }

    private static bool IsHeredocStart(string line)
    {
        return line.Contains("<<", StringComparison.Ordinal);
    }
}

/// <summary>
/// Represents a parsed patch document with ordered file operations.
/// </summary>
/// <param name="Operations">The operations to apply in order.</param>
public sealed record PatchDocument(IReadOnlyList<PatchOperation> Operations);

/// <summary>
/// Base type for a parsed file operation in a structured patch.
/// </summary>
/// <param name="Path">The relative path targeted by the operation.</param>
public abstract record PatchOperation(string Path);

/// <summary>
/// Represents an operation that creates a new file with supplied content.
/// </summary>
/// <param name="Path">The relative path to create.</param>
/// <param name="Content">The file content encoded in the patch.</param>
public sealed record AddFileOperation(string Path, string Content) : PatchOperation(Path);

/// <summary>
/// Represents an operation that deletes a file if it exists.
/// </summary>
/// <param name="Path">The relative path to delete.</param>
public sealed record DeleteFileOperation(string Path) : PatchOperation(Path);

/// <summary>
/// Represents an operation that modifies or moves an existing file.
/// </summary>
/// <param name="Path">The relative source path to update.</param>
/// <param name="MoveTo">The optional relative destination path for a move.</param>
/// <param name="Hunks">The ordered hunks to apply to the source content.</param>
public sealed record UpdateFileOperation(
    string Path,
    string? MoveTo,
    IReadOnlyList<PatchHunk> Hunks) : PatchOperation(Path);

/// <summary>
/// Represents one patch hunk with optional context header and line edits.
/// </summary>
/// <param name="Header">Optional context text used to locate the hunk.</param>
/// <param name="Lines">The ordered context, added, and removed lines.</param>
/// <param name="IsEndOfFile">Whether the hunk explicitly appends or preserves an ending newline.</param>
public sealed record PatchHunk(
    string? Header,
    IReadOnlyList<PatchHunkLine> Lines,
    bool IsEndOfFile);

/// <summary>
/// Represents a single patch hunk line and its operation prefix.
/// </summary>
/// <param name="Prefix">The patch line prefix: space for context, plus for add, or minus for remove.</param>
/// <param name="Text">The line text without the prefix.</param>
public sealed record PatchHunkLine(char Prefix, string Text);
