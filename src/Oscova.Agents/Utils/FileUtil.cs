using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Oscova.Agents.Utils;

/// <summary>
/// File operations shared by reusable toolsets and host infrastructure.
/// </summary>
public static class FileUtil
{
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const int FileRenameInfoEx = 22;
    private const uint RenameReplaceIfExists = 0x1;
    private const uint RenamePosixSemantics = 0x2;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// Writes text to <paramref name="path"/> atomically by staging beside the target before replacing it.
    /// </summary>
    public static async Task WriteAllTextAtomicAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tempPath = CreateSiblingTempPath(path);
        try
        {
            await using (var stream = CreateStagingStream(tempPath, asynchronous: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            // Close staging handles before publication so readers can open the replacement immediately.
            await MoveOverTargetWithRetryAsync(tempPath, path, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Writes a stream to <paramref name="path"/> atomically by staging beside the target before replacing it.</summary>
    public static async Task WriteStreamAtomicAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tempPath = CreateSiblingTempPath(path);
        try
        {
            await using (var stream = CreateStagingStream(tempPath, asynchronous: true))
            {
                await content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            // Close staging handles before publication so readers can open the replacement immediately.
            await MoveOverTargetWithRetryAsync(tempPath, path, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Writes text to <paramref name="path"/> atomically by staging beside the target before replacing it.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tempPath = CreateSiblingTempPath(path);
        try
        {
            using (var stream = CreateStagingStream(tempPath, asynchronous: false))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(content);
                writer.Flush();
            }
            // Close staging handles before publication so readers can open the replacement immediately.
            MoveOverTargetWithRetry(tempPath, path);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Appends a single line through a shared handle so concurrent readers do not fail on file locks.
    /// </summary>
    public static async Task AppendLineSharedAsync(string path, string line, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads all lines through a shared handle so appenders or atomic replacements do not cause file-in-use errors.
    /// A line still being flushed by a concurrent appender may read truncated, so callers must tolerate a malformed last line.
    /// </summary>
    public static string[] ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    /// <summary>Reads all text through a shared handle so atomic replacements do not cause file-in-use errors.</summary>
    public static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Reads all text through a shared handle so atomic replacements do not cause file-in-use errors.</summary>
    public static async Task<string> ReadAllTextSharedAsync(string path, CancellationToken cancellationToken = default)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates a unique temporary path beside <paramref name="path"/> without touching the filesystem.
    /// </summary>
    public static string CreateSiblingTempPath(string path)
    {
        return path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    }

    /// <summary>Formats an absolute Windows path for native file operations.</summary>
    internal static string ToExtendedWindowsPath(string path)
    {
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + path[2..]
            : "\\\\?\\" + path;
    }

    private static FileStream CreateStagingStream(string path, bool asynchronous)
    {
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            // Keep scanners from opening the staging file while its contents are written.
            Share = FileShare.Delete,
            Options = asynchronous ? FileOptions.Asynchronous : FileOptions.None
        });
    }

    private static async Task MoveOverTargetWithRetryAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                PublishStagedFile(sourcePath, targetPath);
                return;
            }
            catch (Exception ex) when (IsTransientReplaceFailure(ex) && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void MoveOverTargetWithRetry(string sourcePath, string targetPath)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                PublishStagedFile(sourcePath, targetPath);
                return;
            }
            catch (Exception ex) when (IsTransientReplaceFailure(ex) && attempt < 3)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * (attempt + 1)));
            }
        }
    }

    /// <summary>Publishes a staged file while preserving replacement-sharing readers' snapshots.</summary>
    private static void PublishStagedFile(string sourcePath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(sourcePath, targetPath, overwrite: true);
            return;
        }

        string source = ToExtendedWindowsPath(Path.GetFullPath(sourcePath));
        string target = ToExtendedWindowsPath(Path.GetFullPath(targetPath));
        using var handle = CreateFile(source, DeleteAccess,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw CreatePublicationException(sourcePath, Marshal.GetLastPInvokeError());
        }

        byte[] name = Encoding.Unicode.GetBytes(target);
        // FILE_RENAME_INFO stores flags, a pointer-aligned root handle, then the name's byte length.
        int lengthOffset = IntPtr.Size * 2;
        int nameOffset = lengthOffset + sizeof(uint);
        byte[] information = new byte[checked(nameOffset + name.Length + sizeof(char))];
        BitConverter.GetBytes(RenameReplaceIfExists | RenamePosixSemantics).CopyTo(information, 0);
        BitConverter.GetBytes(name.Length).CopyTo(information, lengthOffset);
        name.CopyTo(information, nameOffset);

        // Existing readers retain their snapshot when the replacement is published.
        if (!SetFileInformationByHandle(handle, FileRenameInfoEx, information, (uint)information.Length))
        {
            throw CreatePublicationException(targetPath, Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Preserves the native publication failure in the corresponding file exception.</summary>
    private static Exception CreatePublicationException(string path, int error)
    {
        var nativeError = new Win32Exception(error);
        string message = $"Atomic file publication failed for '{path}': {nativeError.Message}";
        return error switch
        {
            ErrorAccessDenied => new UnauthorizedAccessException(message, nativeError),
            ErrorFileNotFound or ErrorPathNotFound => new FileNotFoundException(message, path, nativeError),
            _ => new IOException(message, nativeError)
        };
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int informationClass,
        [In] byte[] information,
        uint bufferSize);

    private static bool IsTransientReplaceFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; the original write failure is what matters.
        }
    }
}
