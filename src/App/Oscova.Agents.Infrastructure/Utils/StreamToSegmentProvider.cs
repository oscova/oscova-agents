using ICU4N.Globalization;
using ICU4N.Text;
using System.Text;

namespace Oscova.Agents.Infrastructure.Utils
{
    /// <param name="locale">e.g., "en", "hi", "mr", "ta"</param>
    /// <param name="tailGuard">Keep this many trailing chars un-emitted to avoid premature boundaries.</param>
    /// <param name="softFlushThreshold">If no sent. boundary found and buffer grows beyond this, do a partial (word) flush.</param>
    /// <param name="softFlushMinKeep">Minimum chars to keep when soft-flushing so context remains.</param>
    public sealed class StreamToSegmentProvider(
    string locale = "en",
    int tailGuard = 24,
    int softFlushThreshold = 400,
    int softFlushMinKeep = 80)
    {
        private readonly UCultureInfo _locale = new(locale);
        private readonly int _tailGuard = Math.Max(0, tailGuard);
        private readonly int _softFlushThreshold = Math.Max(64, softFlushThreshold);
        private readonly int _softFlushMinKeep = Math.Max(0, softFlushMinKeep);
        private readonly StringBuilder _buffer = new();
        private BreakIterator? _breakIterator;

        /// <summary>
        /// Push a new token/chunk. Returns a complete sentence when available; otherwise "".
        /// May return multiple sentences over repeated calls as the buffer grows.
        /// </summary>
        public string Push(string token)
        {
            if (string.IsNullOrEmpty(token)) { return string.Empty; }

            _buffer.Append(token);

            // Try to emit one ready sentence per call (simple contract)
            var text = _buffer.ToString();
            _breakIterator ??= BreakIterator.GetSentenceInstance(_locale);
            _breakIterator.SetText(text);

            // Evaluate only the first sentence boundary; later ones cannot qualify if the first doesn't.
            int start = _breakIterator.First();
            int end = _breakIterator.Next();

            if (end != BreakIterator.Done && end <= text.Length - _tailGuard)
            {
                int consumed = end;
                // emit first ready sentence only
                string sentence = text.Substring(start, end - start);
                _buffer.Remove(0, consumed);
                return sentence;
            }

            // If we still have nothing and the buffer is ballooning, soft-flush by word boundary
            if (_buffer.Length > _softFlushThreshold)
            {
                var s = _buffer.ToString();
                int lastSpace = s.LastIndexOf(' ');
                if (lastSpace >= _softFlushMinKeep)
                {
                    string partial = s[..lastSpace];
                    _buffer.Remove(0, lastSpace);
                    return partial;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Force-flush anything remaining (e.g., at end-of-stream).
        /// Returns "" if nothing remains.
        /// </summary>
        public string Flush()
        {
            if (_buffer.Length == 0) { return string.Empty; }

            string rest = _buffer.ToString();
            _buffer.Clear();
            return rest;
        }
    }
}
