namespace Oscova.Agents.Core.Cli;

/// <summary>Recognizes file errors caused by another process briefly holding the same file.</summary>
public static class FileContention
{
    /// <summary>Returns true for Windows sharing violations or the native Unix would-block errno.</summary>
    public static bool IsContention(IOException exception) => OperatingSystem.IsWindows()
        ? (exception.HResult & 0xffff) is 32 or 33
        : exception.HResult == (OperatingSystem.IsMacOS() ? 35 : 11);
}
