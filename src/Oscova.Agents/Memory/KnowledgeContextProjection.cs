using System;
using System.Linq;
using System.Text;

namespace Oscova.Agents.Memory;

/// <summary>Bounds knowledge text loaded into every run and tells the model which tool reads what was left out.</summary>
/// <remarks>Cuts happen only between lines, so no character is ever split. A line larger than the remaining room is dropped whole.</remarks>
public static class KnowledgeContextProjection
{
    /// <summary>Maximum UTF-8 bytes of one injected index or daily-log section, including its omission line.</summary>
    public const int MaxSectionBytes = 8192;

    /// <summary>Reports whether the text is injected unchanged.</summary>
    public static bool Fits(string text) => Encoding.UTF8.GetByteCount(text) <= MaxSectionBytes;

    /// <summary>Keeps an index's leading lines in index order and ends with the number of omitted entries.</summary>
    public static string KeepHead(string text)
    {
        if (Fits(text)) { return text; }

        var lines = text.Split('\n');
        // Reserve room for the longest possible tail, so the final count always fits.
        var room = MaxSectionBytes - Encoding.UTF8.GetByteCount(OmittedEntries(lines.Length)) - 1;
        var kept = 0;
        for (var used = 0; kept < lines.Length && used + Encoding.UTF8.GetByteCount(lines[kept]) + 1 <= room; kept++)
        {
            used += Encoding.UTF8.GetByteCount(lines[kept]) + 1;
        }

        var omitted = lines.Skip(kept).Count(line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal));
        return string.Join('\n', lines.Take(kept)) + "\n" + OmittedEntries(omitted);
    }

    /// <summary>Keeps a log's trailing lines, its newest entries, behind a line saying earlier entries were omitted.</summary>
    public static string KeepTail(string text)
    {
        if (Fits(text)) { return text; }

        const string head = "Earlier entries omitted, use read_daily";
        var lines = text.Split('\n');
        var room = MaxSectionBytes - Encoding.UTF8.GetByteCount(head) - 1;
        var start = lines.Length;
        for (var used = 0; start > 0 && used + Encoding.UTF8.GetByteCount(lines[start - 1]) + 1 <= room; start--)
        {
            used += Encoding.UTF8.GetByteCount(lines[start - 1]) + 1;
        }

        return head + "\n" + string.Join('\n', lines.Skip(start));
    }

    private static string OmittedEntries(int count) => $"{count} more concepts, use find";
}
