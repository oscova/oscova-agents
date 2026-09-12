using System;
using System.IO;

namespace Oscova.Agents.Persistence;

/// <summary>
/// Splits and combines Markdown files that carry YAML frontmatter at the top.
/// </summary>
public static class MarkdownFrontmatter
{
    /// <summary>
    /// Splits a Markdown document into YAML frontmatter and body text.
    /// </summary>
    /// <remarks>
    /// The closing fence is the first triple-dash line after the opener. A body containing
    /// a standalone triple-dash line before the real body content will be split there.
    /// </remarks>
    public static (string Yaml, string Body) Split(string fileText)
    {
        if (!StartsWithOpeningFence(fileText, out int contentStart))
        {
            return (string.Empty, fileText);
        }

        if (TrySplitAtClosingFence(fileText, contentStart, contentStart, out var split))
        {
            return split;
        }

        var closingFenceStart = fileText.IndexOf("\n---", contentStart, StringComparison.Ordinal);
        while (closingFenceStart >= 0)
        {
            int fenceStart = closingFenceStart + 1;
            if (TrySplitAtClosingFence(fileText, contentStart, fenceStart, out split))
            {
                return split;
            }

            closingFenceStart = fileText.IndexOf("\n---", fenceStart + 3, StringComparison.Ordinal);
        }

        throw new InvalidDataException("YAML frontmatter not terminated.");
    }

    /// <summary>
    /// Combines YAML frontmatter and Markdown body text into a complete document.
    /// </summary>
    public static string Combine(string yaml, string body)
    {
        return $"---\n{yaml.TrimEnd('\r', '\n')}\n---\n{body}";
    }

    private static bool StartsWithOpeningFence(string fileText, out int contentStart)
    {
        contentStart = 0;

        if (!fileText.StartsWith("---", StringComparison.Ordinal))
        {
            return false;
        }

        if (fileText.Length == 3)
        {
            throw new InvalidDataException("YAML frontmatter not terminated.");
        }

        if (fileText[3] == '\n')
        {
            contentStart = 4;
            return true;
        }

        if (fileText[3] == '\r' && fileText.Length > 4 && fileText[4] == '\n')
        {
            contentStart = 5;
            return true;
        }

        return false;
    }

    private static bool TrySplitAtClosingFence(string fileText, int contentStart, int fenceStart, out (string Yaml, string Body) split)
    {
        split = default;

        if (fenceStart + 3 > fileText.Length ||
            fileText[fenceStart] != '-' ||
            fileText[fenceStart + 1] != '-' ||
            fileText[fenceStart + 2] != '-')
        {
            return false;
        }

        int afterFence = fenceStart + 3;
        if (afterFence == fileText.Length)
        {
            split = (TrimSingleTrailingCarriageReturn(fileText[contentStart..fenceStart]), string.Empty);
            return true;
        }

        if (fileText[afterFence] == '\n')
        {
            split = (TrimSingleTrailingCarriageReturn(fileText[contentStart..fenceStart]), fileText[(afterFence + 1)..]);
            return true;
        }

        if (fileText[afterFence] == '\r' &&
            afterFence + 1 < fileText.Length &&
            fileText[afterFence + 1] == '\n')
        {
            split = (TrimSingleTrailingCarriageReturn(fileText[contentStart..fenceStart]), fileText[(afterFence + 2)..]);
            return true;
        }

        return false;
    }

    private static string TrimSingleTrailingCarriageReturn(string value)
    {
        return value.EndsWith('\r') ? value[..^1] : value;
    }
}
