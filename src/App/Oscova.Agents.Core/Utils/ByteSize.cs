namespace Oscova.Agents.Core.Utils;

/// <summary>
/// Shared byte-size units and formatting helpers used across Web, Server, and Infrastructure.
/// </summary>
public static class ByteSize
{
    public const long OneKilobyte = 1024;
    public const long OneMegabyte = OneKilobyte * 1024;
    public const long OneGigabyte = OneMegabyte * 1024;

    /// <summary>
    /// Formats a byte count into a short human-readable string such as KB, MB, or GB.
    /// </summary>
    public static string Format(long? bytes)
    {
        if (bytes is null or 0)
        {
            return "—";
        }

        var value = (double)bytes.Value;
        string[] units = ["B", "KB", "MB", "GB"];
        var unitIndex = 0;

        while (value >= OneKilobyte && unitIndex < units.Length - 1)
        {
            value /= OneKilobyte;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:0} {units[unitIndex]}"
            : $"{value:0.#} {units[unitIndex]}";
    }
}