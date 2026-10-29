using System.Net;
using System.Text.RegularExpressions;
using Markdig;
using System.Globalization;
using System.Text;
using Oscova.Agents.Speech.Contracts;

namespace Oscova.Agents.Core.Speech;

/// <summary>
/// Owns product speech-output targeting and text rules.
/// </summary>
public static class SpeechOutputPolicy
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Converts Markdown fragments into speakable text using the shared speech cleanup rules.</summary>
    public static string SanitizeMarkdownForSpeech(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            // 1) Convert Markdown → HTML using Markdig (already referenced by the project for rendering)
            string html;
            try
            {
                html = Markdown.ToHtml(text);
            }
            catch
            {
                // If the fragment is malformed for markdown, fall back to raw text
                html = text;
            }

            // 2) Light HTML → text normalization via regexes to avoid new deps
            //    - Drop script/style
            html = Regex.Replace(
                html,
                @"<\s*(?<tag>script|style)[^>]*>.*?<\s*/\s*\k<tag>\s*>",
                string.Empty,
                RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
                RegexTimeout);

            //    - Images: speak alt text if present; otherwise drop
            html = Regex.Replace(
                html,
                @"<img[^>]*alt\s*=\s*""(?<alt>[^""]+)""[^>]*>",
                "${alt}",
                RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
                RegexTimeout);

            //    - Links: keep link text only
            html = Regex.Replace(
                html,
                "<a[^>]*>(?<text>.*?)</a>",
                "${text}",
                RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
                RegexTimeout);

            //    - Breaks and common block boundaries → newlines
            html = Regex.Replace(
                html,
                @"<\s*br\s*/?>",
                "\n",
                RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
                RegexTimeout);

            html = Regex.Replace(
                html,
                @"</\s*(h[1-6]|p|div|blockquote|li|ul|ol|pre|table|tr|td|th)\s*>",
                "\n",
                RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
                RegexTimeout);

            html = Regex.Replace(
                html,
                @"<\s*(h[1-6]|p|div|blockquote|li|ul|ol|pre|table|tr|td|th)[^>]*>",
                "\n",
                RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
                RegexTimeout);

            //    - Remove all remaining tags
            var plain = Regex.Replace(
                html,
                "<[^>]+>",
                string.Empty,
                RegexOptions.Singleline | RegexOptions.ExplicitCapture,
                RegexTimeout);

            // 3) HTML decode entities
            plain = WebUtility.HtmlDecode(plain);
            plain = RemoveEmojiTextElements(plain);

            // 4) Remove leading markdown list/heading markers that might slip through on partial fragments
            plain = Regex.Replace(
                plain,
                @"(?m)^\s*[#>*-]{1,6}\s+",
                " ",
                RegexOptions.ExplicitCapture,
                RegexTimeout);

            // 5) Normalize whitespace: collapse newlines and multiple spaces
            plain = Regex.Replace(plain, @"\r\n|\r|\n", " ", RegexOptions.ExplicitCapture, RegexTimeout);
            plain = Regex.Replace(plain, @"\s{2,}", " ", RegexOptions.ExplicitCapture, RegexTimeout);

            // 6) Remove stray backticks often seen in inline code fragments
            plain = plain.Replace("`", string.Empty);

            return plain.Trim();
        }
        catch
        {
            // Best-effort fallback: strip common markdown markers
            var fallback = text
                .Replace("`", string.Empty)
                .Replace("*", string.Empty)
                .Replace("_", string.Empty)
                .Replace("~", string.Empty);
            fallback = RemoveEmojiTextElements(fallback);
            fallback = Regex.Replace(fallback, @"\s{2,}", " ", RegexOptions.ExplicitCapture, RegexTimeout);
            return fallback.Trim();
        }
    }

    /// <summary>Calculates playback duration from audio samples when the provider supplies them.</summary>
    public static double GetClipDurationMs(SpeechSynthesisResponse audio) =>
        audio.TotalSamples > 0 && audio.SampleRate > 0 && audio.Channels > 0
            ? (double)audio.TotalSamples / (audio.SampleRate * audio.Channels) * 1000d
            : audio.DurationMs;

    public static string RemoveEmojiTextElements(string text)
    {
        var result = new StringBuilder(text.Length);
        var elements = StringInfo.GetTextElementEnumerator(text);

        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            // Inspect a complete grapheme so joined emoji and modifiers leave no invalid UTF-16 behind.
            if (!element.EnumerateRunes().Any(rune =>
                    Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol ||
                    rune.Value is 0xFE0F or 0x20E3 or (>= 0x1F3FB and <= 0x1F3FF)))
            {
                result.Append(element);
            }
        }

        return result.ToString();
    }

    public static SpeechOutputTarget ResolveTargets(bool isAdmin, SpeechOutputMode mode)
    {
        if (!isAdmin)
        {
            return SpeechOutputTarget.Browser;
        }

        return mode switch
        {
            SpeechOutputMode.BrowserAndAvatar => SpeechOutputTarget.Browser | SpeechOutputTarget.Avatar,
            SpeechOutputMode.AvatarOnly => SpeechOutputTarget.Avatar,
            _ => SpeechOutputTarget.Browser
        };
    }

    public static bool IncludesBrowser(SpeechOutputTarget targets) =>
        (targets & SpeechOutputTarget.Browser) == SpeechOutputTarget.Browser;

    public static bool IncludesAvatar(SpeechOutputTarget targets) =>
        (targets & SpeechOutputTarget.Avatar) == SpeechOutputTarget.Avatar;
}
