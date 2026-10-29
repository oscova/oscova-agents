using System.Diagnostics;

namespace Oscova.Agents.Core.Cli;

/// <summary>Coordinates shared Avatar files between local processes.</summary>
public static class AvatarFileLease
{
    /// <summary>Attempts exclusive ownership once and returns null only for sharing contention.</summary>
    public static FileStream? TryAcquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (FileContention.IsContention(ex))
        {
            return null;
        }
    }

    /// <summary>Acquires exclusive ownership until the returned stream is disposed.</summary>
    public static async Task<FileStream> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Keep the file after release so every process locks the same filesystem entry.
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (FileContention.IsContention(ex) && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
