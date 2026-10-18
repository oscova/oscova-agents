using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Workspace;

public sealed partial class WorkspaceFileAccess
{
    private const int UnixReadOnly = 0;
    private const int UnixWriteOnly = 1;
    private const int UnixCreateLinux = 0x40;
    private const int UnixCreateDarwin = 0x200;
    private const int UnixExclusiveLinux = 0x80;
    private const int UnixExclusiveDarwin = 0x800;
    private const int UnixAppendLinux = 0x400;
    private const int UnixAppendDarwin = 0x8;
    private const int UnixDirectoryLinux = 0x10000;
    private const int UnixDirectoryDarwin = 0x100000;
    private const int UnixNoFollowLinux = 0x20000;
    private const int UnixNoFollowDarwin = 0x100;
    private const int UnixCloseOnExecLinux = 0x80000;
    private const int UnixCloseOnExecDarwin = 0x1000000;
    private const int AtSymlinkNoFollowLinux = 0x100;
    private const int AtSymlinkNoFollowDarwin = 0x20;
    private const int ErrorNoEntry = 2;
    private const int ErrorPermission = 1;
    private const int ErrorAccess = 13;
    private const int ErrorExists = 17;
    private const int ErrorNotDirectory = 20;
    private const int ErrorLoopLinux = 40;
    private const int ErrorLoopDarwin = 62;
    private const uint DirectoryMode = 0x4000;
    private const uint SymbolicLinkMode = 0xA000;
    private const uint FileTypeMask = 0xF000;
    private const uint DefaultCreateMode = 0x1B6; // 0666, restricted by the process umask.
    private const uint DefaultDirectoryMode = 0x1FF; // 0777, restricted by the process umask.

    private static Stream OpenReadUnix(ResolvedWorkspacePath path)
    {
        using UnixOpenedParent parent = OpenParentUnix(path, createParents: false);
        int descriptor = OpenAt(parent.DirectoryDescriptor, parent.TargetName, UnixReadOnly | UnixNoFollow | UnixCloseOnExec);
        if (descriptor < 0) throw CreateUnixException("Open", path.FullPath, Marshal.GetLastPInvokeError());

        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void WriteUnix(ResolvedWorkspacePath path, Action<Stream> write, bool append, bool createNew)
    {
        using UnixOpenedParent parent = OpenParentUnix(path, createParents: !createNew);
        if (append || createNew)
        {
            int descriptor = OpenAt(
                parent.DirectoryDescriptor,
                parent.TargetName,
                UnixWriteOnly | UnixCreate | (createNew ? UnixExclusive : UnixAppend) | UnixNoFollow | UnixCloseOnExec,
                DefaultCreateMode);
            if (descriptor < 0) throw CreateUnixException("Open", path.FullPath, Marshal.GetLastPInvokeError());

            using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            using var stream = new FileStream(handle, FileAccess.Write);
            // Positioned writes do not honor O_APPEND on macOS.
            if (append) { stream.Seek(0, SeekOrigin.End); }
            write(stream);
            stream.Flush();
            return;
        }

        string temporaryName = Path.GetFileName(FileUtil.CreateSiblingTempPath(parent.TargetName));
        try
        {
            int descriptor = OpenAt(
                parent.DirectoryDescriptor,
                temporaryName,
                UnixWriteOnly | UnixCreate | UnixExclusive | UnixNoFollow | UnixCloseOnExec,
                DefaultCreateMode);
            if (descriptor < 0) throw CreateUnixException("Create", path.FullPath, Marshal.GetLastPInvokeError());

            using (var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true))
            using (var stream = new FileStream(handle, FileAccess.Write))
            {
                write(stream);
                stream.Flush();
            }

            if (RenameAt(parent.DirectoryDescriptor, temporaryName, parent.DirectoryDescriptor, parent.TargetName) != 0)
            {
                throw CreateUnixException("Replace", path.FullPath, Marshal.GetLastPInvokeError());
            }
        }
        catch
        {
            _ = UnlinkAt(parent.DirectoryDescriptor, temporaryName, 0);
            throw;
        }
    }

    private static void DeleteUnix(ResolvedWorkspacePath path)
    {
        using UnixOpenedParent parent = OpenParentUnix(path, createParents: false);
        if (UnlinkAt(parent.DirectoryDescriptor, parent.TargetName, 0) == 0) return;

        int error = Marshal.GetLastPInvokeError();
        if (error != ErrorNoEntry) throw CreateUnixException("Delete", path.FullPath, error);
    }

    private static WorkspaceFileEntry? GetEntryUnix(ResolvedWorkspacePath path)
    {
        if (path.RelativePath == ".")
        {
            using UnixDirectoryHandle root = OpenDirectoryUnix(path, ".", createMissing: false);
            return TryGetUnixMetadata(root.Descriptor, null, path.FullPath, out WorkspaceFileEntry? entry)
                ? entry
                : null;
        }

        if (PathHelper.ArePathsEqual(path.FullPath, path.AuthorizedRoot))
        {
            using UnixDirectoryHandle root = OpenDirectoryUnix(path, path.RelativePath, createMissing: false);
            return TryGetUnixMetadata(root.Descriptor, null, path.FullPath, out WorkspaceFileEntry? entry)
                ? entry
                : null;
        }

        using UnixOpenedParent parent = OpenParentUnix(path, createParents: false);
        return TryGetUnixMetadata(parent.DirectoryDescriptor, parent.TargetName, path.FullPath, out WorkspaceFileEntry? result)
            ? result
            : null;
    }

    private static IEnumerable<WorkspaceFileEntry> EnumerateUnix(ResolvedWorkspacePath path)
    {
        using UnixDirectoryHandle directory = OpenDirectoryUnix(path, path.RelativePath, createMissing: false);
        int descriptor = directory.Detach();
        IntPtr stream = FdOpenDirectory(descriptor);
        if (stream == IntPtr.Zero)
        {
            int error = Marshal.GetLastPInvokeError();
            _ = Close(descriptor);
            throw CreateUnixException("Enumerate", path.FullPath, error);
        }

        try
        {
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                IntPtr current = ReadDirectory(stream);
                if (current == IntPtr.Zero)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != 0) throw CreateUnixException("Enumerate", path.FullPath, error);
                    yield break;
                }

                string name = ReadDirectoryEntryName(current);
                if (name is "." or "..") continue;

                string entryPath = Path.Combine(path.FullPath, name);
                if (TryGetUnixMetadata(descriptor, name, entryPath, out WorkspaceFileEntry? entry) && entry is not null)
                {
                    yield return entry;
                }
            }
        }
        finally
        {
            _ = CloseDirectory(stream);
        }
    }

    private static UnixOpenedParent OpenParentUnix(ResolvedWorkspacePath path, bool createParents)
    {
        string[] components = GetRelativeComponents(path.RelativePath);
        if (components.Length == 0)
        {
            throw new UnauthorizedAccessException($"A workspace root cannot be used as a file: {path.FullPath}");
        }

        if (PathHelper.ArePathsEqual(path.FullPath, path.AuthorizedRoot))
        {
            throw new UnauthorizedAccessException($"A workspace root cannot be used as a file: {path.FullPath}");
        }

        string parentPath = components.Length == 1
            ? "."
            : Path.Combine(components[..^1]);
        UnixDirectoryHandle directory = OpenDirectoryUnix(path, parentPath, createParents);
        return new UnixOpenedParent(directory, components[^1]);
    }

    private static UnixDirectoryHandle OpenDirectoryUnix(
        ResolvedWorkspacePath path,
        string relativePath,
        bool createMissing)
    {
        int descriptor = Open(path.TraversalRoot, UnixReadOnly | UnixDirectory | UnixCloseOnExec);
        if (descriptor < 0) throw CreateUnixException("Open workspace", path.TraversalRoot, Marshal.GetLastPInvokeError());

        try
        {
            string[] components = GetRelativeComponents(relativePath);
            for (int index = 0; index < components.Length; index++)
            {
                string component = components[index];
                // Every untrusted descendant is opened relative to its held parent and cannot be a link.
                int next = OpenAt(descriptor, component, UnixReadOnly | UnixDirectory | UnixNoFollow | UnixCloseOnExec);
                if (next < 0 &&
                    createMissing &&
                    index >= path.RequiredExistingComponents &&
                    Marshal.GetLastPInvokeError() == ErrorNoEntry)
                {
                    if (MakeDirectoryAt(descriptor, component, DefaultDirectoryMode) != 0 &&
                        Marshal.GetLastPInvokeError() != ErrorExists)
                    {
                        throw CreateUnixException("Create directory", component, Marshal.GetLastPInvokeError());
                    }

                    next = OpenAt(descriptor, component, UnixReadOnly | UnixDirectory | UnixNoFollow | UnixCloseOnExec);
                }

                if (next < 0) throw CreateUnixException("Open directory", component, Marshal.GetLastPInvokeError());
                _ = Close(descriptor);
                descriptor = next;
            }

            return new UnixDirectoryHandle(descriptor);
        }
        catch
        {
            _ = Close(descriptor);
            throw;
        }
    }

    private static bool TryGetUnixMetadata(
        int directoryDescriptor,
        string? name,
        string fullPath,
        out WorkspaceFileEntry? entry)
    {
        int result;
        uint mode;
        long length;
        long modifiedSeconds;
        long modifiedNanoseconds;

        if (OperatingSystem.IsMacOS())
        {
            DarwinStat metadata;
            result = name is null
                ? FStatDarwin(directoryDescriptor, out metadata)
                : FStatAtDarwin(directoryDescriptor, name, out metadata, AtSymlinkNoFollow);
            mode = metadata.Mode;
            length = metadata.Size;
            modifiedSeconds = metadata.Modified.Seconds;
            modifiedNanoseconds = metadata.Modified.Nanoseconds;
        }
        else
        {
            LinuxStat metadata;
            result = name is null
                ? FStatLinux(directoryDescriptor, out metadata)
                : FStatAtLinux(directoryDescriptor, name, out metadata, AtSymlinkNoFollow);
            mode = metadata.Mode;
            length = metadata.Size;
            modifiedSeconds = metadata.ModifiedSeconds;
            modifiedNanoseconds = metadata.ModifiedNanoseconds;
        }

        if (result != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNoEntry)
            {
                entry = null;
                return false;
            }

            throw CreateUnixException("Read metadata", fullPath, error);
        }

        uint type = mode & FileTypeMask;
        FileAttributes attributes = type switch
        {
            DirectoryMode => FileAttributes.Directory,
            SymbolicLinkMode => FileAttributes.ReparsePoint,
            _ => FileAttributes.Normal
        };
        DateTime modified = DateTimeOffset.FromUnixTimeSeconds(modifiedSeconds)
            .AddTicks(modifiedNanoseconds / 100)
            .UtcDateTime;
        entry = new WorkspaceFileEntry(fullPath, attributes, type == SymbolicLinkMode ? 0 : length, modified);
        return true;
    }

    private static string[] GetRelativeComponents(string relativePath)
    {
        if (relativePath == ".") return [];

        string[] components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (Array.Exists(components, component => !PathHelper.IsSafePathSegment(component)))
        {
            throw new UnauthorizedAccessException($"Path contains an invalid segment: {relativePath}");
        }

        return components;
    }

    private static string ReadDirectoryEntryName(IntPtr entry)
    {
        int offset;
        int length;
        if (OperatingSystem.IsMacOS())
        {
            offset = 21;
            length = Marshal.ReadInt16(entry, 18);
        }
        else
        {
            offset = 19;
            int recordLength = Marshal.ReadInt16(entry, 16) & 0xFFFF;
            length = Math.Max(0, recordLength - offset);
        }

        byte[] bytes = new byte[length];
        Marshal.Copy(IntPtr.Add(entry, offset), bytes, 0, length);
        int terminator = Array.IndexOf(bytes, (byte)0);
        if (terminator >= 0) length = terminator;
        return System.Text.Encoding.UTF8.GetString(bytes, 0, length);
    }

    private static Exception CreateUnixException(string operation, string path, int error)
    {
        string message = $"{operation} failed for '{path}': {new Win32Exception(error).Message}";
        if (error == ErrorNoEntry) return new FileNotFoundException(message, path);
        if (error is ErrorPermission or ErrorAccess or ErrorNotDirectory ||
            error == (OperatingSystem.IsMacOS() ? ErrorLoopDarwin : ErrorLoopLinux))
        {
            return new UnauthorizedAccessException(message);
        }

        return new IOException(message);
    }

    private static int UnixCreate => OperatingSystem.IsMacOS() ? UnixCreateDarwin : UnixCreateLinux;
    private static int UnixExclusive => OperatingSystem.IsMacOS() ? UnixExclusiveDarwin : UnixExclusiveLinux;
    private static int UnixAppend => OperatingSystem.IsMacOS() ? UnixAppendDarwin : UnixAppendLinux;
    private static int UnixDirectory => OperatingSystem.IsMacOS() ? UnixDirectoryDarwin : UnixDirectoryLinux;
    private static int UnixNoFollow => OperatingSystem.IsMacOS() ? UnixNoFollowDarwin : UnixNoFollowLinux;
    private static int UnixCloseOnExec => OperatingSystem.IsMacOS() ? UnixCloseOnExecDarwin : UnixCloseOnExecLinux;
    private static int AtSymlinkNoFollow => OperatingSystem.IsMacOS() ? AtSymlinkNoFollowDarwin : AtSymlinkNoFollowLinux;
    // Intel's unsuffixed exports use legacy inode layouts, unlike Apple Silicon.
    private static bool IsDarwinX64 => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private sealed class UnixOpenedParent(UnixDirectoryHandle directory, string targetName) : IDisposable
    {
        public int DirectoryDescriptor => directory.Descriptor;
        public string TargetName { get; } = targetName;
        public void Dispose() => directory.Dispose();
    }

    private sealed class UnixDirectoryHandle(int descriptor) : IDisposable
    {
        private int _descriptor = descriptor;
        public int Descriptor => _descriptor;

        public int Detach()
        {
            int detachedDescriptor = _descriptor;
            _descriptor = -1;
            return detachedDescriptor;
        }

        public void Dispose()
        {
            if (_descriptor < 0) return;
            _ = Close(_descriptor);
            _descriptor = -1;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong Device;
        public ulong Inode;
        public ulong Links;
        public uint Mode;
        public uint User;
        public uint Group;
        public int Padding;
        public ulong RawDevice;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public long AccessedSeconds;
        public long AccessedNanoseconds;
        public long ModifiedSeconds;
        public long ModifiedNanoseconds;
        public long ChangedSeconds;
        public long ChangedNanoseconds;
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinTimespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinStat
    {
        public int Device;
        public ushort Mode;
        public ushort Links;
        public ulong Inode;
        public uint User;
        public uint Group;
        public int RawDevice;
        public DarwinTimespec Accessed;
        public DarwinTimespec Modified;
        public DarwinTimespec Changed;
        public DarwinTimespec Created;
        public long Size;
        public long Blocks;
        public int BlockSize;
        public uint Flags;
        public uint Generation;
        public int Spare;
        public long Spare0;
        public long Spare1;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private static int OpenAt(int directoryDescriptor, string path, int flags, uint mode) =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? OpenAtDarwin(directoryDescriptor, path, flags, 0, 0, 0, 0, 0, mode)
            : OpenAtUnix(directoryDescriptor, path, flags, mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtUnix(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    // Apple arm64 puts variadic arguments on the stack. Fill x3-x7 so mode occupies the first stack slot.
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtDarwin(
        int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags,
        nint unused3, nint unused4, nint unused5, nint unused6, nint unused7, uint mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MakeDirectoryAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int RenameAt(
        int oldDirectoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr FdOpenDirectoryNative(int descriptor);

    [DllImport("libc", EntryPoint = "fdopendir$INODE64", SetLastError = true)]
    private static extern IntPtr FdOpenDirectoryDarwinX64(int descriptor);

    private static IntPtr FdOpenDirectory(int descriptor) =>
        IsDarwinX64 ? FdOpenDirectoryDarwinX64(descriptor) : FdOpenDirectoryNative(descriptor);

    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static extern IntPtr ReadDirectoryNative(IntPtr directory);

    [DllImport("libc", EntryPoint = "readdir$INODE64", SetLastError = true)]
    private static extern IntPtr ReadDirectoryDarwinX64(IntPtr directory);

    private static IntPtr ReadDirectory(IntPtr directory) =>
        IsDarwinX64 ? ReadDirectoryDarwinX64(directory) : ReadDirectoryNative(directory);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int CloseDirectory(IntPtr directory);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStatLinux(int descriptor, out LinuxStat metadata);

    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)]
    private static extern int FStatAtLinux(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out LinuxStat metadata,
        int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStatDarwinNative(int descriptor, out DarwinStat metadata);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int FStatDarwinX64(int descriptor, out DarwinStat metadata);

    private static int FStatDarwin(int descriptor, out DarwinStat metadata) =>
        IsDarwinX64 ? FStatDarwinX64(descriptor, out metadata) : FStatDarwinNative(descriptor, out metadata);

    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)]
    private static extern int FStatAtDarwinNative(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out DarwinStat metadata,
        int flags);

    [DllImport("libc", EntryPoint = "fstatat$INODE64", SetLastError = true)]
    private static extern int FStatAtDarwinX64(
        int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out DarwinStat metadata, int flags);

    private static int FStatAtDarwin(int directoryDescriptor, string path, out DarwinStat metadata, int flags) =>
        IsDarwinX64 ? FStatAtDarwinX64(directoryDescriptor, path, out metadata, flags)
            : FStatAtDarwinNative(directoryDescriptor, path, out metadata, flags);
}
