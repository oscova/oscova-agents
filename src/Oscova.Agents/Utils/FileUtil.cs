using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Utils;

/// <summary>
/// File operations shared by reusable toolsets and host infrastructure.
/// </summary>
public static class FileUtil
{
    /// <summary>
    /// Writes text to <paramref name="path"/> atomically by staging beside the target before replacing it.
    /// </summary>
    public static async Task WriteAllTextAtomicAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tempPath = CreateSiblingTempPath(path);
        try
        {
            await using var stream = CreateStagingStream(tempPath, asynchronous: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
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
            await using var stream = CreateStagingStream(tempPath, asynchronous: true);
            await content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
            using var stream = CreateStagingStream(tempPath, asynchronous: false);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(content);
            writer.Flush();
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

    /// <summary>
    /// Creates a unique temporary path beside <paramref name="path"/> without touching the filesystem.
    /// </summary>
    public static string CreateSiblingTempPath(string path)
    {
        return path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    }

    private static FileStream CreateStagingStream(string path, bool asynchronous)
    {
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            // Keep scanners from opening the staging file before its same-directory rename.
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
                File.Move(sourcePath, targetPath, overwrite: true);
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
                File.Move(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsTransientReplaceFailure(ex) && attempt < 3)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * (attempt + 1)));
            }
        }
    }

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