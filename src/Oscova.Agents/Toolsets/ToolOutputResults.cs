using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Oscova.Agents.Toolsets;

/// <summary>Counts results while retaining an inline slice and a bounded saved prefix.</summary>
internal sealed class ToolOutputResults(int inlineLimit)
{
    private static readonly int SeparatorBytes = Encoding.UTF8.GetByteCount(Environment.NewLine);
    private int _spillBytes;
    private bool _spillFull;

    public List<string> Retained { get; } = [];
    public int Total { get; private set; }
    public int SpillCount { get; private set; }
    public bool Truncated => SpillCount < Total;

    public void Record(string entry)
    {
        Total++;
        if (!_spillFull)
        {
            int cost = Encoding.UTF8.GetByteCount(entry) + SeparatorBytes;
            if (_spillBytes + cost <= BoundedOutputBuffer.DefaultCapacityBytes)
            {
                _spillBytes += cost;
                SpillCount++;
                Retained.Add(entry);
                return;
            }

            _spillFull = true;
        }

        // Oversized entries stay visible within the requested inline slice but never enter the file.
        if (Retained.Count < inlineLimit) { Retained.Add(entry); }
    }

    public string Compose(IToolOutputStore? store, string label)
    {
        if (Total <= inlineLimit)
        {
            return string.Join(Environment.NewLine, Retained);
        }

        string? path = SpillCount == 0
            ? null
            : store?.Save(label, string.Join(Environment.NewLine, Retained.Take(SpillCount)));
        string notice = path is null
            ? $"[Showing {inlineLimit} of {Total} results.]"
            : Truncated
                ? $"[Showing {inlineLimit} of {Total} results. First {SpillCount} results saved to {path}]"
                : $"[Showing {inlineLimit} of {Total} results. Full results saved to {path}]";
        return string.Join(Environment.NewLine, Retained.Take(inlineLimit)) + Environment.NewLine + Environment.NewLine + notice;
    }
}
