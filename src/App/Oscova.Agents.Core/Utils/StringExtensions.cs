namespace Oscova.Agents.Core.Utils;

public static class StringExtensions
{
    /// <summary>Returns <see langword="null"/> if <paramref name="value"/> is null or whitespace; otherwise returns the trimmed value.</summary>
    public static string? NormalizeOptional(this string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
