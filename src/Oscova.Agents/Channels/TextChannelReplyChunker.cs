using System;
using System.Collections.Generic;

namespace Oscova.Agents.Channels;

/// <summary>Splits remote-channel replies without breaking UTF-16 surrogate pairs.</summary>
public static class TextChannelReplyChunker
{
    /// <summary>Splits text into chunks bounded by maxLength UTF-16 code units.</summary>
    public static IEnumerable<string> Split(string text, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);

        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(maxLength, text.Length - offset);
            if (length > 1 &&
                offset + length < text.Length &&
                char.IsHighSurrogate(text[offset + length - 1]) &&
                char.IsLowSurrogate(text[offset + length]))
            {
                length--;
            }

            yield return text.Substring(offset, length);
            offset += length;
        }
    }
}
