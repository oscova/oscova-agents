using System.Security.Cryptography;
using System.Text;

namespace Oscova.Agents.Core.Cli;

/// <summary>Coordinates local startup and destructive lifecycle operations outside persistent user data.</summary>
public static class LocalLifecycleLease
{
    /// <summary>Returns the private coordination directory shared by processes using one CLI root.</summary>
    public static string GetDirectory(string cliRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cliRoot));
        if (OperatingSystem.IsWindows()) { root = root.ToUpperInvariant(); }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
        var directory = Path.Combine(Path.GetTempPath(), "oscova-lifecycle-" + key);
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Lifecycle coordination directory cannot be a link: '{directory}'.");
        }

        CliFileSecurity.EnsurePrivateDirectory(directory);
        return directory;
    }

    /// <summary>Waits briefly for another lifecycle owner using the existing file-lease policy.</summary>
    public static Task<FileStream> AcquireAsync(string cliRoot, CancellationToken cancellationToken) =>
        AvatarFileLease.AcquireAsync(Path.Combine(GetDirectory(cliRoot), "lifecycle.lock"), cancellationToken);

    /// <summary>Returns null immediately when startup or another lifecycle operation owns the gate.</summary>
    public static FileStream? TryAcquire(string cliRoot) =>
        AvatarFileLease.TryAcquire(Path.Combine(GetDirectory(cliRoot), "lifecycle.lock"));
}
