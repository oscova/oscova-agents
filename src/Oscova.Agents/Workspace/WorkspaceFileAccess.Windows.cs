using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Workspace;

public sealed partial class WorkspaceFileAccess
{
    private const uint WindowsDelete = 0x00010000;
    private const uint WindowsSynchronize = 0x00100000;
    private const uint WindowsReadData = 0x00000001;
    private const uint WindowsWriteData = 0x00000002;
    private const uint WindowsAppendData = 0x00000004;
    private const uint WindowsReadAttributes = 0x00000080;
    private const uint WindowsShareRead = 0x00000001;
    private const uint WindowsShareWrite = 0x00000002;
    private const uint WindowsShareDelete = 0x00000004;
    private const uint WindowsOpen = 0x00000001;
    private const uint WindowsCreate = 0x00000002;
    private const uint WindowsOpenIf = 0x00000003;
    private const uint WindowsDirectoryFile = 0x00000001;
    private const uint WindowsSynchronousIo = 0x00000020;
    private const uint WindowsNonDirectoryFile = 0x00000040;
    private const uint WindowsOpenReparsePoint = 0x00200000;
    private const uint WindowsBackupSemantics = 0x02000000;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsObjectCaseInsensitive = 0x00000040;
    private const int FileBasicInformationClass = 0;
    private const int FileStandardInformationClass = 1;
    private const int FileDirectoryInformationClass = 1;
    private const int FileRenameInformationClass = 10;
    private const int FileDispositionInformationClass = 13;
    private const int FileAttributeTagInformationClass = 9;
    private const int StatusNoMoreFiles = unchecked((int)0x80000006);
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
    private const int StatusNoSuchFile = unchecked((int)0xC000000F);
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;

    private static Stream OpenReadWindows(ResolvedWorkspacePath path)
    {
        using WindowsOpenedParent parent = OpenParentWindows(path, createParents: false);
        SafeFileHandle handle = OpenRelativeWindows(
            parent.DirectoryHandle,
            parent.TargetName,
            WindowsReadData | WindowsReadAttributes | WindowsSynchronize,
            WindowsOpen,
            WindowsNonDirectoryFile | WindowsOpenReparsePoint | WindowsSynchronousIo,
            WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
            path.FullPath);
        try
        {
            RejectReparsePoint(handle, path.FullPath);
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void WriteWindows(ResolvedWorkspacePath path, Action<Stream> write, bool append)
    {
        using WindowsOpenedParent parent = OpenParentWindows(path, createParents: true);
        if (append)
        {
            using SafeFileHandle handle = OpenRelativeWindows(
                parent.DirectoryHandle,
                parent.TargetName,
                WindowsAppendData | WindowsReadAttributes | WindowsSynchronize,
                WindowsOpenIf,
                WindowsNonDirectoryFile | WindowsOpenReparsePoint | WindowsSynchronousIo,
                WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
                path.FullPath);
            RejectReparsePoint(handle, path.FullPath);
            using var stream = new FileStream(handle, FileAccess.Write);
            write(stream);
            stream.Flush();
            return;
        }

        string temporaryName = Path.GetFileName(FileUtil.CreateSiblingTempPath(parent.TargetName));
        try
        {
            using SafeFileHandle handle = OpenRelativeWindows(
                parent.DirectoryHandle,
                temporaryName,
                WindowsWriteData | WindowsDelete | WindowsReadAttributes | WindowsSynchronize,
                WindowsCreate,
                WindowsNonDirectoryFile | WindowsOpenReparsePoint | WindowsSynchronousIo,
                WindowsShareRead | WindowsShareDelete,
                path.FullPath);
            using (var stream = new FileStream(handle, FileAccess.Write))
            {
                write(stream);
                stream.Flush();
                ReplaceRelativeWindows(handle, parent.DirectoryHandle, parent.TargetName, path.FullPath);
            }
        }
        catch
        {
            TryDeleteRelativeWindows(parent.DirectoryHandle, temporaryName);
            throw;
        }
    }

    private static void DeleteWindows(ResolvedWorkspacePath path)
    {
        using WindowsOpenedParent parent = OpenParentWindows(path, createParents: false);
        if (!TryOpenRelativeWindows(
                parent.DirectoryHandle,
                parent.TargetName,
                WindowsDelete | WindowsReadAttributes | WindowsSynchronize,
                WindowsOpen,
                WindowsNonDirectoryFile | WindowsOpenReparsePoint | WindowsSynchronousIo,
                WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
                out SafeFileHandle? handle,
                out int status))
        {
            if (IsWindowsMissing(status)) return;
            throw CreateWindowsException("Delete", path.FullPath, status);
        }

        SafeFileHandle openedHandle = handle!;
        using (openedHandle)
        {
            SetDeleteDisposition(openedHandle, path.FullPath);
        }
    }

    private static WorkspaceFileEntry? GetEntryWindows(ResolvedWorkspacePath path)
    {
        if (path.RelativePath == ".")
        {
            using SafeFileHandle root = OpenWindowsRoot(path.TraversalRoot);
            return ReadWindowsEntry(root, path.FullPath);
        }

        if (PathHelper.ArePathsEqual(path.FullPath, path.AuthorizedRoot))
        {
            using SafeFileHandle root = OpenDirectoryWindows(path, path.RelativePath, createMissing: false);
            return ReadWindowsEntry(root, path.FullPath);
        }

        using WindowsOpenedParent parent = OpenParentWindows(path, createParents: false);
        if (!TryOpenRelativeWindows(
                parent.DirectoryHandle,
                parent.TargetName,
                WindowsReadAttributes | WindowsSynchronize,
                WindowsOpen,
                WindowsOpenReparsePoint | WindowsSynchronousIo,
                WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
                out SafeFileHandle? handle,
                out int status))
        {
            if (IsWindowsMissing(status)) return null;
            throw CreateWindowsException("Read metadata", path.FullPath, status);
        }

        SafeFileHandle openedHandle = handle!;
        using (openedHandle)
        {
            return ReadWindowsEntry(openedHandle, path.FullPath);
        }
    }

    private static IEnumerable<WorkspaceFileEntry> EnumerateWindows(ResolvedWorkspacePath path)
    {
        using SafeFileHandle directory = OpenDirectoryWindows(path, path.RelativePath, createMissing: false);
        IntPtr buffer = Marshal.AllocHGlobal(64 * 1024);
        try
        {
            bool restart = true;
            while (true)
            {
                var ioStatus = new IoStatusBlock();
                int status = NtQueryDirectoryFile(
                    directory,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    ref ioStatus,
                    buffer,
                    64 * 1024,
                    FileDirectoryInformationClass,
                    returnSingleEntry: false,
                    IntPtr.Zero,
                    restart);
                restart = false;
                if (status == StatusNoMoreFiles) yield break;
                if (status < 0) throw CreateWindowsException("Enumerate", path.FullPath, status);

                int offset = 0;
                while (true)
                {
                    IntPtr entry = IntPtr.Add(buffer, offset);
                    uint nextOffset = unchecked((uint)Marshal.ReadInt32(entry, 0));
                    long lastWrite = Marshal.ReadInt64(entry, 24);
                    long length = Marshal.ReadInt64(entry, 40);
                    var attributes = (FileAttributes)unchecked((uint)Marshal.ReadInt32(entry, 56));
                    int nameLength = Marshal.ReadInt32(entry, 60);
                    string name = Marshal.PtrToStringUni(IntPtr.Add(entry, 64), nameLength / 2) ?? string.Empty;
                    if (name is not "." and not "..")
                    {
                        yield return new WorkspaceFileEntry(
                            Path.Combine(path.FullPath, name),
                            attributes,
                            attributes.HasFlag(FileAttributes.ReparsePoint) ? 0 : length,
                            DateTime.FromFileTimeUtc(lastWrite));
                    }

                    if (nextOffset == 0) break;
                    offset += checked((int)nextOffset);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static WindowsOpenedParent OpenParentWindows(ResolvedWorkspacePath path, bool createParents)
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
        SafeFileHandle directory = OpenDirectoryWindows(path, parentPath, createParents);
        return new WindowsOpenedParent(directory, components[^1]);
    }

    private static SafeFileHandle OpenDirectoryWindows(
        ResolvedWorkspacePath path,
        string relativePath,
        bool createMissing)
    {
        SafeFileHandle current = OpenWindowsRoot(path.TraversalRoot);
        try
        {
            string[] components = GetRelativeComponents(relativePath);
            for (int index = 0; index < components.Length; index++)
            {
                string component = components[index];
                // No delete sharing pins each parent until its verified child handle is acquired.
                bool opened = TryOpenRelativeWindows(
                        current,
                        component,
                        WindowsReadData | WindowsReadAttributes | WindowsSynchronize,
                        WindowsOpen,
                        WindowsDirectoryFile | WindowsOpenReparsePoint | WindowsSynchronousIo,
                        WindowsShareRead | WindowsShareWrite,
                        out SafeFileHandle? next,
                        out int status);
                if (!opened &&
                    createMissing &&
                    index >= path.RequiredExistingComponents &&
                    IsWindowsMissing(status))
                {
                    opened = TryOpenRelativeWindows(
                            current,
                            component,
                            WindowsReadData | WindowsReadAttributes | WindowsSynchronize,
                            WindowsCreate,
                            WindowsDirectoryFile | WindowsOpenReparsePoint | WindowsSynchronousIo,
                            WindowsShareRead | WindowsShareWrite,
                            out next,
                            out status);
                    if (!opened && status == StatusObjectNameCollision)
                    {
                        next = OpenRelativeWindows(
                            current,
                            component,
                            WindowsReadData | WindowsReadAttributes | WindowsSynchronize,
                            WindowsOpen,
                            WindowsDirectoryFile | WindowsOpenReparsePoint | WindowsSynchronousIo,
                            WindowsShareRead | WindowsShareWrite,
                            component);
                        opened = true;
                    }
                }

                if (!opened || next?.IsInvalid != false)
                {
                    next?.Dispose();
                    throw CreateWindowsException("Open directory", component, status);
                }

                try
                {
                    RejectReparsePoint(next, component);
                    current.Dispose();
                    current = next;
                }
                catch
                {
                    next.Dispose();
                    throw;
                }
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenWindowsRoot(string path)
    {
        SafeFileHandle handle = CreateFile(
            ToExtendedWindowsPath(path),
            WindowsReadData | WindowsReadAttributes | WindowsSynchronize,
            WindowsShareRead | WindowsShareWrite,
            IntPtr.Zero,
            WindowsOpenExisting,
            WindowsBackupSemantics,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;

        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw CreateWindowsExceptionFromError("Open workspace", path, error);
    }

    private static SafeFileHandle OpenRelativeWindows(
        SafeFileHandle parent,
        string name,
        uint access,
        uint disposition,
        uint options,
        uint share,
        string displayPath)
    {
        if (TryOpenRelativeWindows(parent, name, access, disposition, options, share, out SafeFileHandle? handle, out int status))
        {
            return handle!;
        }

        throw CreateWindowsException("Open", displayPath, status);
    }

    private static bool TryOpenRelativeWindows(
        SafeFileHandle parent,
        string name,
        uint access,
        uint disposition,
        uint options,
        uint share,
        out SafeFileHandle? handle,
        out int status)
    {
        IntPtr nameBuffer = Marshal.StringToHGlobalUni(name);
        IntPtr unicodeStringPointer = IntPtr.Zero;
        try
        {
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                Buffer = nameBuffer
            };
            unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeStringPointer,
                Attributes = WindowsObjectCaseInsensitive
            };
            var ioStatus = new IoStatusBlock();
            status = NtCreateFile(
                out SafeFileHandle opened,
                access,
                ref attributes,
                ref ioStatus,
                IntPtr.Zero,
                (uint)FileAttributes.Normal,
                share,
                disposition,
                options,
                IntPtr.Zero,
                0);
            if (status >= 0)
            {
                handle = opened;
                return true;
            }

            opened?.Dispose();
            handle = null;
            return false;
        }
        finally
        {
            if (unicodeStringPointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodeStringPointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static void RejectReparsePoint(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInformationClass,
                out FileAttributeTagInformation information,
                Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw CreateWindowsExceptionFromError("Read metadata", path, Marshal.GetLastPInvokeError());
        }

        if (((FileAttributes)information.FileAttributes).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException($"Path contains a reparse point: {path}");
        }
    }

    private static WorkspaceFileEntry ReadWindowsEntry(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileBasicInformationClass,
                out FileBasicInformation basic,
                Marshal.SizeOf<FileBasicInformation>()) ||
            !GetFileInformationByHandleEx(
                handle,
                FileStandardInformationClass,
                out FileStandardInformation standard,
                Marshal.SizeOf<FileStandardInformation>()))
        {
            throw CreateWindowsExceptionFromError("Read metadata", path, Marshal.GetLastPInvokeError());
        }

        var attributes = (FileAttributes)basic.FileAttributes;
        return new WorkspaceFileEntry(
            path,
            attributes,
            attributes.HasFlag(FileAttributes.ReparsePoint) ? 0 : standard.EndOfFile,
            DateTime.FromFileTimeUtc(basic.LastWriteTime));
    }

    private static void ReplaceRelativeWindows(
        SafeFileHandle source,
        SafeFileHandle targetDirectory,
        string targetName,
        string displayPath)
    {
        byte[] nameBytes = System.Text.Encoding.Unicode.GetBytes(targetName);
        int headerSize = Marshal.SizeOf<FileRenameInformationHeader>();
        int rootOffset = Marshal.OffsetOf<FileRenameInformationHeader>(nameof(FileRenameInformationHeader.RootDirectory)).ToInt32();
        int nameLengthOffset = Marshal.OffsetOf<FileRenameInformationHeader>(nameof(FileRenameInformationHeader.FileNameLength)).ToInt32();
        int nameOffset = nameLengthOffset + sizeof(uint);
        int bufferSize = checked(headerSize + nameBytes.Length);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.WriteByte(buffer, 0, 1);
            for (int index = 1; index < rootOffset; index++) Marshal.WriteByte(buffer, index, 0);
            Marshal.WriteIntPtr(buffer, rootOffset, targetDirectory.DangerousGetHandle());
            Marshal.WriteInt32(buffer, nameLengthOffset, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, nameOffset), nameBytes.Length);

            for (int attempt = 0; ; attempt++)
            {
                var ioStatus = new IoStatusBlock();
                int status = NtSetInformationFile(
                    source,
                    ref ioStatus,
                    buffer,
                    bufferSize,
                    FileRenameInformationClass);
                if (status >= 0) return;
                if (attempt >= 3) throw CreateWindowsException("Replace", displayPath, status);
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * (attempt + 1)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void TryDeleteRelativeWindows(SafeFileHandle parent, string name)
    {
        if (!TryOpenRelativeWindows(
                parent,
                name,
                WindowsDelete | WindowsReadAttributes | WindowsSynchronize,
                WindowsOpen,
                WindowsOpenReparsePoint | WindowsSynchronousIo,
                WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
                out SafeFileHandle? handle,
                out _))
        {
            return;
        }

        SafeFileHandle openedHandle = handle!;
        using (openedHandle)
        {
            try
            {
                SetDeleteDisposition(openedHandle, name);
            }
            catch
            {
                // Best-effort staging cleanup preserves the original write failure.
            }
        }
    }

    private static void SetDeleteDisposition(SafeFileHandle handle, string displayPath)
    {
        IntPtr buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            var ioStatus = new IoStatusBlock();
            int status = NtSetInformationFile(handle, ref ioStatus, buffer, 1, FileDispositionInformationClass);
            if (status < 0) throw CreateWindowsException("Delete", displayPath, status);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsWindowsMissing(int status)
    {
        return status is StatusObjectNameNotFound or StatusObjectPathNotFound or StatusNoSuchFile;
    }

    private static Exception CreateWindowsException(string operation, string path, int status)
    {
        if (IsWindowsMissing(status)) return new FileNotFoundException($"{operation} failed for '{path}'.", path);
        if (status == StatusAccessDenied) return new UnauthorizedAccessException($"{operation} was denied for '{path}'.");
        return CreateWindowsExceptionFromError(operation, path, unchecked((int)RtlNtStatusToDosError(status)));
    }

    private static Exception CreateWindowsExceptionFromError(string operation, string path, int error)
    {
        string message = $"{operation} failed for '{path}': {new Win32Exception(error).Message}";
        if (error is ErrorFileNotFound or ErrorPathNotFound) return new FileNotFoundException(message, path);
        if (error == ErrorAccessDenied) return new UnauthorizedAccessException(message);
        return new IOException(message, unchecked((int)(0x80070000u | (uint)error)));
    }

    private static string ToExtendedWindowsPath(string path)
    {
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) return path;
        return path.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + path[2..]
            : "\\\\?\\" + path;
    }

    private sealed class WindowsOpenedParent(SafeFileHandle directoryHandle, string targetName) : IDisposable
    {
        public SafeFileHandle DirectoryHandle { get; } = directoryHandle;
        public string TargetName { get; } = targetName;
        public void Dispose() => DirectoryHandle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] public bool Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInformationHeader
    {
        [MarshalAs(UnmanagedType.U1)] public bool ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileAttributeTagInformation information,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileBasicInformation information,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileStandardInformation information,
        int bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        ref IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr extendedAttributes,
        uint extendedAttributesLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(
        SafeFileHandle fileHandle,
        IntPtr eventHandle,
        IntPtr apcRoutine,
        IntPtr apcContext,
        ref IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        int length,
        int fileInformationClass,
        [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
        IntPtr fileName,
        [MarshalAs(UnmanagedType.U1)] bool restartScan);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle,
        ref IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        int length,
        int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
