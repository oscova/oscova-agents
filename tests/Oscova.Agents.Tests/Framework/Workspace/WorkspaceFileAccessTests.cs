using System.Text;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tests.Framework.Workspace;

/// <summary>Verifies physical workspace containment through real filesystem operations.</summary>
public sealed class WorkspaceFileAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workspace-access-tests", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly WorkspaceFileAccess _files;

    public WorkspaceFileAccessTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);
        IReadOnlyList<AllowedDirectory> grants =
        [
            new(_workspace, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ];
        _files = new WorkspaceFileAccess(() => grants);
    }

    [Theory]
    [InlineData("nested", "notes.txt")]
    [InlineData("nested", "..notes.txt")]
    [InlineData("..cache", "notes.txt")]
    public void Operations_PreserveOrdinaryFileContract(string directory, string fileName)
    {
        string path = Path.Combine(_workspace, directory, fileName);

        WriteText(path, "alpha");
        WriteText(path, "\nbeta", append: true);

        using (Stream stream = _files.OpenRead(path))
        using (var reader = new StreamReader(stream))
        {
            Assert.True(stream.CanSeek);
            Assert.Equal("alpha\nbeta", reader.ReadToEnd());
        }

        WorkspaceFileEntry entry = Assert.IsType<WorkspaceFileEntry>(_files.GetEntry(path));
        Assert.Equal(10, entry.Length);
        Assert.Contains(_files.Enumerate(Path.GetDirectoryName(path)!), item => item.Path == path);

        _files.Delete(path);
        Assert.Null(_files.GetEntry(path));
    }

    [Fact]
    public void WriteNew_PreservesExistingFilesAndRequiresAnExistingParent()
    {
        var path = Path.Combine(_workspace, "new.txt");
        _files.WriteNew(path, stream => stream.Write(Encoding.UTF8.GetBytes("original")));
        Assert.ThrowsAny<IOException>(() => _files.WriteNew(path, stream => stream.WriteByte(9)));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.ThrowsAny<IOException>(() => _files.WriteNew(Path.Combine(_workspace, "missing", "new.txt"), stream => stream.WriteByte(9)));
        Assert.False(Directory.Exists(Path.Combine(_workspace, "missing")));
    }

    [Fact]
    public void Write_WhenCallbackFails_PreservesDestinationAndRemovesStagingFile()
    {
        string path = Path.Combine(_workspace, "notes.txt");
        WriteText(path, "original");

        Assert.Throws<InvalidOperationException>(() => _files.Write(path, stream =>
        {
            stream.Write("replacement"u8);
            throw new InvalidOperationException("test failure");
        }));

        Assert.Equal("original", File.ReadAllText(path));
        Assert.Equal(["notes.txt"], Directory.GetFiles(_workspace).Select(Path.GetFileName));
    }

    [SkippableFact]
    public void Write_OnUnix_PreservesCreationModeForReplacementAndAppend()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.If(true, "Requires Unix file permissions.");
            return;
        }

        string reference = Path.Combine(_workspace, "reference.txt");
        File.WriteAllText(reference, "reference");
        UnixFileMode expectedMode = File.GetUnixFileMode(reference);
        string target = Path.Combine(_workspace, "notes.txt");
        WriteText(target, "first");
        Assert.Equal(expectedMode, File.GetUnixFileMode(target));
        WriteText(target, "replacement");
        Assert.Equal(expectedMode, File.GetUnixFileMode(target));
        string appended = Path.Combine(_workspace, "appended.txt");
        WriteText(appended, "append", append: true);
        Assert.Equal(expectedMode, File.GetUnixFileMode(appended));
        using var replacedReader = new StreamReader(_files.OpenRead(target));
        using var appendedReader = new StreamReader(_files.OpenRead(appended));
        Assert.Equal("replacement", replacedReader.ReadToEnd());
        Assert.Equal("append", appendedReader.ReadToEnd());
    }

    [Fact]
    public void Write_UsesNestedReadOnlyGrantEvenWhenOuterGrantIsWritable()
    {
        string nested = Path.Combine(_workspace, "reference");
        Directory.CreateDirectory(nested);
        IReadOnlyList<AllowedDirectory> grants =
        [
            new(_workspace, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite),
            new(nested, "reference", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly)
        ];
        var files = new WorkspaceFileAccess(() => grants);

        Assert.Throws<UnauthorizedAccessException>(() =>
            files.Write(Path.Combine(nested, "blocked.txt"), stream => stream.Write("blocked"u8)));
        Assert.False(File.Exists(Path.Combine(nested, "blocked.txt")));
    }

    [Fact]
    public void Write_WhenWritableNestedGrantIsMissing_DoesNotCreateItInsideReadOnlyOuterGrant()
    {
        string nested = Path.Combine(_workspace, "writable");
        IReadOnlyList<AllowedDirectory> grants =
        [
            new(_workspace, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly),
            new(nested, "writable", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ];
        var files = new WorkspaceFileAccess(() => grants);

        Assert.Throws<FileNotFoundException>(() =>
            files.Write(Path.Combine(nested, "notes.txt"), stream => stream.Write("blocked"u8)));
        Assert.False(Directory.Exists(nested));
    }

    [Fact]
    public void GetEntry_WhenPathIsNestedGrantRoot_ReturnsDirectory()
    {
        string nested = Path.Combine(_workspace, "nested-grant");
        Directory.CreateDirectory(nested);
        IReadOnlyList<AllowedDirectory> grants =
        [
            new(_workspace, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly),
            new(nested, "nested", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ];
        var files = new WorkspaceFileAccess(() => grants);

        WorkspaceFileEntry entry = Assert.IsType<WorkspaceFileEntry>(files.GetEntry(nested));

        Assert.True(entry.Attributes.HasFlag(FileAttributes.Directory));
    }

    [Fact]
    public void Delete_WhenParentIsMissing_IsNoOp()
    {
        string missingParent = Path.Combine(_workspace, "missing");

        _files.Delete(Path.Combine(missingParent, "notes.txt"));

        Assert.False(Directory.Exists(missingParent));
    }

    [Fact]
    public void Delete_WhenTargetIsDirectory_RejectsAndPreservesDirectory()
    {
        string directory = Path.Combine(_workspace, "directory");
        Directory.CreateDirectory(directory);

        Exception exception = Assert.ThrowsAny<Exception>(() => _files.Delete(directory));
        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(directory));
    }

    [SkippableFact]
    public void OpenRead_WhenParentWasReplacedByLink_RejectsOutsideContent()
    {
        string parent = Path.Combine(_workspace, "parent");
        string movedParent = Path.Combine(_workspace, "moved-parent");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(parent, "notes.txt"), "inside");
        File.WriteAllText(Path.Combine(outside, "notes.txt"), "outside");

        ReplaceDirectoryWithLink(parent, movedParent, outside);

        Assert.Throws<UnauthorizedAccessException>(() => _files.OpenRead(Path.Combine(parent, "notes.txt")));
    }

    [SkippableFact]
    public void OpenRead_WhenNestedGrantRootWasReplaced_StillWalksFromOuterGrant()
    {
        string nested = Path.Combine(_workspace, "nested");
        string movedNested = Path.Combine(_workspace, "moved-nested");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(nested, "notes.txt"), "inside");
        File.WriteAllText(Path.Combine(outside, "notes.txt"), "outside");
        IReadOnlyList<AllowedDirectory> grants =
        [
            new(_workspace, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite),
            new(nested, "nested", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ];
        var files = new WorkspaceFileAccess(() => grants);

        ReplaceDirectoryWithLink(nested, movedNested, outside);

        Assert.Throws<UnauthorizedAccessException>(() => files.OpenRead(Path.Combine(nested, "notes.txt")));
    }

    [SkippableFact]
    public void OpenRead_HoldsOriginalFileAfterParentIsRelinked()
    {
        string parent = Path.Combine(_workspace, "parent");
        string movedParent = Path.Combine(_workspace, "moved-parent");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        string path = Path.Combine(parent, "notes.txt");
        File.WriteAllText(path, "inside");
        File.WriteAllText(Path.Combine(outside, "notes.txt"), "outside");

        using Stream stream = _files.OpenRead(path);
        try
        {
            ReplaceDirectoryWithLink(parent, movedParent, outside);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // Some Windows filesystems prevent renaming a directory while a child file is open.
        }
        using var reader = new StreamReader(stream);

        Assert.Equal("inside", reader.ReadToEnd());
    }

    [SkippableFact]
    public void Write_WhenParentChangesDuringCallback_NeverWritesOutsideGrant()
    {
        string parent = Path.Combine(_workspace, "parent");
        string movedParent = Path.Combine(_workspace, "moved-parent");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        string path = Path.Combine(parent, "notes.txt");

        bool replacementPrevented = false;
        _files.Write(path, stream =>
        {
            stream.Write("safe"u8);
            try
            {
                ReplaceDirectoryWithLink(parent, movedParent, outside);
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                replacementPrevented = true;
            }
        });

        Assert.False(File.Exists(Path.Combine(outside, "notes.txt")));
        Assert.True(replacementPrevented || File.ReadAllText(Path.Combine(movedParent, "notes.txt")) == "safe");
    }

    [SkippableFact]
    public void Enumerate_WhenDirectoryChangesAfterFirstEntry_NeverDisclosesOutsideNames()
    {
        string parent = Path.Combine(_workspace, "parent");
        string movedParent = Path.Combine(_workspace, "moved-parent");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(parent, "alpha.txt"), "inside");
        File.WriteAllText(Path.Combine(parent, "beta.txt"), "inside");
        File.WriteAllText(Path.Combine(outside, "outside-secret.txt"), "outside");

        using IEnumerator<WorkspaceFileEntry> iterator = _files.Enumerate(parent).GetEnumerator();
        Assert.True(iterator.MoveNext());
        try
        {
            ReplaceDirectoryWithLink(parent, movedParent, outside);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // The held Windows handle prevents renaming the enumerated directory.
        }

        var names = new List<string> { Path.GetFileName(iterator.Current.Path) };
        while (iterator.MoveNext()) names.Add(Path.GetFileName(iterator.Current.Path));
        Assert.DoesNotContain("outside-secret.txt", names);
    }

    public void Dispose()
    {
        string parent = Path.Combine(_workspace, "parent");
        if (Directory.Exists(parent) && new DirectoryInfo(parent).LinkTarget is not null) Directory.Delete(parent);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void WriteText(string path, string content, bool append = false)
    {
        _files.Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(content);
        }, append);
    }

    private static void ReplaceDirectoryWithLink(string path, string movedPath, string target)
    {
        try
        {
            Directory.Move(path, movedPath);
            Directory.CreateSymbolicLink(path, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (Directory.Exists(movedPath) && !Directory.Exists(path)) Directory.Move(movedPath, path);
            Skip.If(true, $"Directory links are unavailable: {exception.Message}");
        }
    }
}
