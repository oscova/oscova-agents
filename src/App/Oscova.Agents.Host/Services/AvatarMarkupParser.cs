using System.Text;
using System.Text.RegularExpressions;

namespace Oscova.Agents.Host.Services;

/// <summary>An emotion cue at a fraction of its clip's cue-free text.</summary>
public sealed record AvatarMarkupCue(string Emotion, double Fraction);

/// <summary>A raw speech clip with its executed tags removed and its cues extracted.</summary>
public sealed record AvatarMarkupClip(string Text, IReadOnlyList<AvatarMarkupCue> Cues);

/// <summary>Extracts Oscova Avatar Markup emotion cues from the raw clips of one reply.</summary>
public sealed partial class AvatarMarkupParser(IReadOnlyList<string> emotions)
{
    // Curated emoji, applied only when the Engine catalog has the target emotion.
    private static readonly (string Emoji, string Emotion)[] EmojiMap =
    [
        ("😊", "Happy"), ("🙂", "Happy"), ("😄", "Happy"), ("😀", "Happy"), ("😁", "Happy"),
        ("😢", "Sad"), ("😞", "Sad"), ("😔", "Sad"), ("😭", "Sad"),
        ("🤨", "Skeptical"), ("🧐", "Skeptical"),
        ("😮", "Surprised"), ("😲", "Surprised"),
        ("😠", "Angry"), ("😡", "Angry")
    ];

    // Code fences can open in one clip and close in a later one.
    private bool _inFence;
    // Length of the backtick run that opened the current code span, 0 outside one. Spans can cross clips.
    private int _codeTicks;

    /// <summary>Returns reply text as chat shows it, with executed tags and a trailing partial tag removed.</summary>
    public static string StripForDisplay(string text, IReadOnlyList<string> emotions)
    {
        if (emotions.Count == 0)
        {
            return text;
        }

        var clean = new AvatarMarkupParser(emotions).Parse(text).Text;
        // A tag still streaming in must not flash as text.
        return PartialTagPattern().Replace(clean, string.Empty);
    }

    /// <summary>Parses the next raw clip of the reply, before any Markdown cleanup.</summary>
    public AvatarMarkupClip Parse(string clip)
    {
        var output = new StringBuilder(clip.Length);
        var cues = new List<(string Emotion, int Position, bool FromEmoji)>();
        var index = 0;
        while (index < clip.Length)
        {
            if (index == 0 || clip[index - 1] == '\n')
            {
                var lineEnd = clip.IndexOf('\n', index);
                var line = lineEnd < 0 ? clip[index..] : clip[index..(lineEnd + 1)];
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
                {
                    _inFence = !_inFence;
                    output.Append(line);
                    index += line.Length;
                    continue;
                }
            }

            var current = clip[index];
            if (_inFence)
            {
                output.Append(current);
                index++;
                continue;
            }

            if (current == '`')
            {
                var runLength = 1;
                while (index + runLength < clip.Length && clip[index + runLength] == '`')
                {
                    runLength++;
                }

                if (_codeTicks == 0)
                {
                    _codeTicks = runLength;
                }
                else if (runLength == _codeTicks)
                {
                    _codeTicks = 0;
                }

                output.Append(clip.AsSpan(index, runLength));
                index += runLength;
                continue;
            }
            else if (_codeTicks == 0 && current == '\\' && index + 1 < clip.Length && clip[index + 1] == '[')
            {
                // Keep the escape so Markdown renders it as a literal bracket.
                output.Append("\\[");
                index += 2;
                continue;
            }
            else if (_codeTicks == 0 && current == '[' && TagPattern().Match(clip, index) is { Success: true } tag &&
                FindEmotion(tag.Groups["name"].Value) is { } name)
            {
                cues.Add((name, output.Length, false));
                index += tag.Length;
                // Drop one following space so the offset matches the spoken text.
                if (index < clip.Length && clip[index] == ' ' && (output.Length == 0 || output[^1] == ' '))
                {
                    index++;
                }

                continue;
            }
            else if (_codeTicks == 0 && EmojiAt(clip, index) is { } emoji)
            {
                if (FindEmotion(emoji.Emotion) is { } emojiName)
                {
                    cues.Add((emojiName, output.Length, true));
                }

                output.Append(emoji.Emoji);
                index += emoji.Emoji.Length;
                continue;
            }

            output.Append(current);
            index++;
        }

        // An explicit tag wins over emoji in the same clip.
        if (cues.Exists(cue => !cue.FromEmoji))
        {
            cues.RemoveAll(cue => cue.FromEmoji);
        }

        var length = output.Length;
        var result = cues.ConvertAll(cue => new AvatarMarkupCue(cue.Emotion, length == 0 ? 0 : (double)cue.Position / length));
        return new AvatarMarkupClip(output.ToString(), result);
    }

    // Returns the catalog spelling so the Engine receives its own name.
    private string? FindEmotion(string name) =>
        emotions.FirstOrDefault(emotion => string.Equals(emotion, name, StringComparison.OrdinalIgnoreCase));

    private static (string Emoji, string Emotion)? EmojiAt(string text, int index)
    {
        foreach (var entry in EmojiMap)
        {
            if (string.CompareOrdinal(text, index, entry.Emoji, 0, entry.Emoji.Length) == 0)
            {
                return entry;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\G\[emotion:(?<name>[A-Za-z][A-Za-z0-9_-]{0,31})\]", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"(?<!\\)\[(?:e(?:m(?:o(?:t(?:i(?:o(?:n(?::[A-Za-z0-9_-]{0,32})?)?)?)?)?)?)?)?\z", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex PartialTagPattern();
}
