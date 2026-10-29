namespace Oscova.Agents.Host.Utils
{
    /// <summary>
    /// Helper methods for common string operations.
    /// </summary>
    public static class StringUtils
    {
        /// <summary>
        /// Capitalizes the first character of the input and returns the full string. If the input is null or empty, returns the input.
        /// </summary>
        /// <param name="value">Input text.</param>
        /// <returns>String with the first character capitalized; otherwise, the original input.</returns>
        public static string Capitalize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return char.ToUpper(value[0]) + value.Substring(1);
        }

        /// <summary>
        /// Generates upper-case initials from a person's name for thumbnails.
        /// - Multi-word names: first letter of the first and last words (e.g., "Peter Parker" → "PP").
        /// - Single-word names: first two alphanumeric letters (e.g., "Plato" → "PL").
        /// </summary>
        /// <param name="name">The person's name.</param>
        /// <param name="separator">Optional separator between initials (e.g., " "). Defaults to empty.</param>
        /// <returns>Upper-case initials, or an empty string if none found.</returns>
        public static string GetInitials(string name, string separator = "")
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var parts = name.Trim().Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return string.Empty;
            }

            if (parts.Length == 1)
            {
                var word = parts[0];
                var buffer = new char[2];
                var count = 0;
                for (int i = 0; i < word.Length && count < 2; i++)
                {
                    var ch = word[i];
                    if (char.IsLetterOrDigit(ch))
                    {
                        buffer[count++] = char.ToUpperInvariant(ch);
                    }
                }

                return count == 0 ? string.Empty : new string(buffer, 0, count);
            }

            var first = char.ToUpperInvariant(parts[0][0]);
            var last = char.ToUpperInvariant(parts[^1][0]);
            return string.IsNullOrEmpty(separator)
                ? new string([first, last])
                : string.Concat(first, separator, last);
        }
    }
}
