using System.Text;

namespace Oscova.Agents.Toolsets;

/// <summary>Shapes oversized text results and saves their recoverable content.</summary>
public static class ToolOutput
{
    /// <summary>The inline character budget shared by tools that trim long text.</summary>
    public const int InlineTextChars = 16_000;

    /// <summary>Returns text unchanged when it fits, otherwise its start with a saved-output notice.</summary>
    public static string LimitText(IToolOutputStore? store, string label, string text)
    {
        if (text.Length <= InlineTextChars)
        {
            return text;
        }

        int cut = SafeStartCut(text, InlineTextChars);
        string payload = BoundedPayload(text);
        string? path = store?.Save(label, payload);
        string notice = path is null
            ? $"[Showing the first {cut} of {text.Length} characters.]"
            : payload.Length == text.Length
                ? $"[Showing the first {cut} of {text.Length} characters. Full output saved to {path}]"
                : $"[Showing the first {cut} of {text.Length} characters. First {payload.Length} characters saved to {path}]";
        return text[..cut] + "\n\n" + notice;
    }

    /// <summary>Returns long command output with its start, end, and captured-output notice.</summary>
    public static string LimitCommandOutput(IToolOutputStore? store, string label, string text, bool alreadyTruncated)
    {
        if (text.Length <= InlineTextChars)
        {
            return text;
        }

        int head = SafeStartCut(text, InlineTextChars / 2);
        int tail = SafeEndCut(text, text.Length - InlineTextChars / 2);
        string payload = BoundedPayload(text);
        string? path = store?.Save(label, payload);
        string saved = payload.Length != text.Length
            ? $"First {payload.Length} characters saved to {path}"
            : alreadyTruncated ? $"Captured output saved to {path}" : $"Full output saved to {path}";
        string notice;
        if (alreadyTruncated)
        {
            notice = path is null
                ? $"[Showing the first {head} and last {text.Length - tail} of {text.Length} captured characters. The shell may have dropped part of the original output before capture.]"
                : $"[Showing the first {head} and last {text.Length - tail} of {text.Length} captured characters. {saved}. The shell may have dropped part of the original output before capture, and that part is not in the file.]";
        }
        else
        {
            notice = path is null
                ? $"[Showing the first {head} and last {text.Length - tail} of {text.Length} characters.]"
                : $"[Showing the first {head} and last {text.Length - tail} of {text.Length} characters. {saved}]";
        }

        return text[..head] + "\n\n" + notice + "\n\n" + text[tail..];
    }

    private static int SafeStartCut(string text, int cut) =>
        char.IsHighSurrogate(text[cut - 1]) && char.IsLowSurrogate(text[cut]) ? cut - 1 : cut;

    private static int SafeEndCut(string text, int cut) =>
        char.IsLowSurrogate(text[cut]) && char.IsHighSurrogate(text[cut - 1]) ? cut + 1 : cut;

    private static string BoundedPayload(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= BoundedOutputBuffer.DefaultCapacityBytes)
        {
            return text;
        }

        int bytes = 0;
        int chars = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > BoundedOutputBuffer.DefaultCapacityBytes)
            {
                break;
            }

            bytes += rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }

        return text[..chars];
    }
}
