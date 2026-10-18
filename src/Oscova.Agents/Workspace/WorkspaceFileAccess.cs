using System;
using System.Collections.Generic;
using System.IO;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Workspace;

/// <summary>Performs contained workspace I/O through held filesystem handles.</summary>
public sealed partial class WorkspaceFileAccess : IWorkspaceFileAccess
{
    private readonly Func<IReadOnlyList<AllowedDirectory>> _workspaceResolver;

    /// <summary>Creates workspace access backed by the current directory grants.</summary>
    public WorkspaceFileAccess(Func<IReadOnlyList<AllowedDirectory>> workspaceResolver)
    {
        _workspaceResolver = workspaceResolver ?? throw new ArgumentNullException(nameof(workspaceResolver));
    }

    /// <inheritdoc />
    public Stream OpenRead(string path)
    {
        ResolvedWorkspacePath resolved = Resolve(path, WorkspacePathAccess.Read);
        Stream stream = OpenReadPlatform(resolved);
        if (stream.CanSeek) return stream;

        stream.Dispose();
        throw new IOException($"Workspace file is not seekable: {path}");
    }

    /// <inheritdoc />
    public void Write(string path, Action<Stream> write, bool append = false)
    {
        ArgumentNullException.ThrowIfNull(write);
        ResolvedWorkspacePath resolved = Resolve(path, WorkspacePathAccess.Write);
        WritePlatform(resolved, write, append);
    }

    /// <summary>Creates a new file in an existing authorized directory, failing if the destination already exists.</summary>
    public void WriteNew(string path, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ResolvedWorkspacePath resolved = Resolve(path, WorkspacePathAccess.Write);
        WritePlatform(resolved, write, append: false, createNew: true);
    }

    /// <inheritdoc />
    public void Delete(string path)
    {
        ResolvedWorkspacePath resolved = Resolve(path, WorkspacePathAccess.Write);
        try
        {
            DeletePlatform(resolved);
        }
        catch (FileNotFoundException)
        {
            // Deleting a missing file is intentionally idempotent.
        }
    }

    /// <inheritdoc />
    public WorkspaceFileEntry? GetEntry(string path)
    {
        ResolvedWorkspacePath resolved = Resolve(path, WorkspacePathAccess.Read);
        try
        {
            return GetEntryPlatform(resolved);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public IEnumerable<WorkspaceFileEntry> Enumerate(string directory)
    {
        ResolvedWorkspacePath resolved = Resolve(directory, WorkspacePathAccess.Read);
        return EnumeratePlatform(resolved);
    }

    private ResolvedWorkspacePath Resolve(string path, WorkspacePathAccess access)
    {
        IReadOnlyList<AllowedDirectory> grants = _workspaceResolver();
        string fullPath = PathHelper.ResolveAuthorizedHostPath(
            path,
            grants,
            access,
            out string traversalRoot,
            out string authorizedRoot);
        string relativePath = Path.GetRelativePath(traversalRoot, fullPath);
        string authorizedRootRelativePath = Path.GetRelativePath(traversalRoot, authorizedRoot);
        int requiredExistingComponents = GetRelativeComponents(authorizedRootRelativePath).Length;
        return new ResolvedWorkspacePath(fullPath, traversalRoot, authorizedRoot, relativePath, requiredExistingComponents);
    }

    private static Stream OpenReadPlatform(ResolvedWorkspacePath path)
    {
        if (OperatingSystem.IsWindows()) return OpenReadWindows(path);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return OpenReadUnix(path);
        throw new PlatformNotSupportedException("Contained workspace access supports Windows, Linux, and macOS.");
    }

    private static void WritePlatform(ResolvedWorkspacePath path, Action<Stream> write, bool append, bool createNew = false)
    {
        if (OperatingSystem.IsWindows())
        {
            WriteWindows(path, write, append, createNew);
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            WriteUnix(path, write, append, createNew);
            return;
        }

        throw new PlatformNotSupportedException("Contained workspace access supports Windows, Linux, and macOS.");
    }

    private static void DeletePlatform(ResolvedWorkspacePath path)
    {
        if (OperatingSystem.IsWindows())
        {
            DeleteWindows(path);
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            DeleteUnix(path);
            return;
        }

        throw new PlatformNotSupportedException("Contained workspace access supports Windows, Linux, and macOS.");
    }

    private static WorkspaceFileEntry? GetEntryPlatform(ResolvedWorkspacePath path)
    {
        if (OperatingSystem.IsWindows()) return GetEntryWindows(path);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return GetEntryUnix(path);
        throw new PlatformNotSupportedException("Contained workspace access supports Windows, Linux, and macOS.");
    }

    private static IEnumerable<WorkspaceFileEntry> EnumeratePlatform(ResolvedWorkspacePath path)
    {
        if (OperatingSystem.IsWindows()) return EnumerateWindows(path);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return EnumerateUnix(path);
        throw new PlatformNotSupportedException("Contained workspace access supports Windows, Linux, and macOS.");
    }

    private readonly record struct ResolvedWorkspacePath(
        string FullPath,
        string TraversalRoot,
        string AuthorizedRoot,
        string RelativePath,
        int RequiredExistingComponents);
}
