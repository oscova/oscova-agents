using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tests.Framework.Utilities;

public sealed class PathHelperTests
{
    [Fact]
    public void ArePathsEqual_ReturnsTrueForEquivalentFullPaths()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "workspace-root");
        string childPath = Path.Combine(rootPath, "child");
        string equivalentPath = Path.Combine(rootPath, ".", "child");

        Assert.True(PathHelper.ArePathsEqual(childPath, equivalentPath));
    }

    [Fact]
    public void ArePathsEqual_IgnoresTrailingDirectorySeparator()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "workspace-root");
        string pathWithSeparator = rootPath + Path.DirectorySeparatorChar;

        Assert.True(PathHelper.ArePathsEqual(rootPath, pathWithSeparator));
    }

    [Fact]
    public void ArePathsEqual_PreservesFilesystemRoot()
    {
        string filesystemRoot = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.True(PathHelper.ArePathsEqual(filesystemRoot, filesystemRoot));
    }

    [Fact]
    public void ArePathsEqual_ReturnsFalseForDifferentPaths()
    {
        string firstPath = Path.Combine(Path.GetTempPath(), "workspace-root-one");
        string secondPath = Path.Combine(Path.GetTempPath(), "workspace-root-two");

        Assert.False(PathHelper.ArePathsEqual(firstPath, secondPath));
    }

    [Fact]
    public void ArePathsEqual_ReturnsFalseForBlankPaths()
    {
        Assert.False(PathHelper.ArePathsEqual(string.Empty, Path.GetTempPath()));
        Assert.False(PathHelper.ArePathsEqual(Path.GetTempPath(), " "));
    }

    [Fact]
    public void ToSlashSeparatedRelativePath_NormalizesBackslashes()
    {
        Assert.Equal("scripts/run.py", PathHelper.ToSlashSeparatedRelativePath(@"scripts\run.py"));
    }

    [Fact]
    public void GetRelativePathUnderRoot_RejectsTraversalOutsideRoot()
    {
        string? result = PathHelper.GetRelativePathUnderRoot(
            "workspace/library/../default/foo.md",
            "workspace/library",
            StringComparison.OrdinalIgnoreCase);

        Assert.Null(result);
    }

    [Fact]
    public void ToPlatformRelativePath_UsesCurrentDirectorySeparator()
    {
        Assert.Equal(
            Path.Combine("scripts", "run.py"),
            PathHelper.ToPlatformRelativePath("scripts/run.py"));
    }

    [Fact]
    public void IsDirectChildOfAnyDirectory_MatchesOnlyDirectChildren()
    {
        string[] directories = ["scripts", "references"];

        Assert.True(PathHelper.IsDirectChildOfAnyDirectory("scripts/run.py", directories));
        Assert.True(PathHelper.IsDirectChildOfAnyDirectory(@"references\guide.md", directories));
        Assert.False(PathHelper.IsDirectChildOfAnyDirectory("scripts/nested/run.py", directories));
        Assert.False(PathHelper.IsDirectChildOfAnyDirectory("assets/config.json", directories));
        Assert.False(PathHelper.IsDirectChildOfAnyDirectory("SKILL.md", directories));
    }

    [Fact]
    public void TryGetContainingDirectory_PrefersMostSpecificNestedDirectory()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "workspace-root");
        string nestedPath = Path.Combine(rootPath, "private");
        string filePath = Path.Combine(nestedPath, "notes.txt");

        AllowedDirectory[] allowedDirectories =
        [
            new(rootPath, "root", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite),
            new(nestedPath, "private", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly)
        ];

        bool found = PathHelper.TryGetContainingDirectory(filePath, allowedDirectories, out AllowedDirectory? containingDirectory);

        Assert.True(found);
        AllowedDirectory selectedDirectory = Assert.IsType<AllowedDirectory>(containingDirectory);
        Assert.Equal(nestedPath, selectedDirectory.Value);
        Assert.Equal(WorkspaceAccessMode.ReadOnly, selectedDirectory.AccessMode);
    }

    [Fact]
    public void TryGetContainingDirectory_PrefersReadOnlyWhenSamePathAppearsTwice()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "workspace-root");
        string filePath = Path.Combine(rootPath, "notes.txt");

        AllowedDirectory[] allowedDirectories =
        [
            new(rootPath, "root-rw", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite),
            new(rootPath, "root-ro", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly)
        ];

        bool found = PathHelper.TryGetContainingDirectory(filePath, allowedDirectories, out AllowedDirectory? containingDirectory);

        Assert.True(found);
        AllowedDirectory selectedDirectory = Assert.IsType<AllowedDirectory>(containingDirectory);
        Assert.Equal(WorkspaceAccessMode.ReadOnly, selectedDirectory.AccessMode);
    }

    [Fact]
    public void MountTargets_ForSameLabel_LandOnDistinctRoots()
    {
        // A workspace and an external directory sharing a name mount to different roots, so the agent
        // editor must not flag them as a collision: /workspace/docs is not /mnt/docs.
        Assert.Equal("/workspace/docs", PathHelper.ToWorkspaceAgentPath("docs"));
        Assert.Equal("/mnt/docs", PathHelper.ToExtraMountAgentPath("docs"));
        Assert.NotEqual(PathHelper.ToWorkspaceAgentPath("docs"), PathHelper.ToExtraMountAgentPath("docs"));
    }

    [Fact]
    public void ToWorkspaceAgentPath_WithContainerDelimiter_UsesMountSafeName()
    {
        Assert.Equal("/workspace/notes-2026", PathHelper.ToWorkspaceAgentPath("notes:2026"));
    }

    [Fact]
    public void TryResolveToHostPath_MapsWorkspaceAgentPathToHostPath()
    {
        string workspacePath = Path.Combine(Path.GetTempPath(), "workspace-default");
        AllowedDirectory[] allowedDirectories =
        [
            new(workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/workspace/default")
        ];

        bool resolved = PathHelper.TryResolveToHostPath(
            "/workspace/default/out.txt",
            allowedDirectories,
            workspacePath,
            out string hostPath);

        Assert.True(resolved);
        Assert.Equal(Path.GetFullPath(Path.Combine(workspacePath, "out.txt")), hostPath);
    }

    [Fact]
    public void TryResolveToHostPath_MapsDataAgentPathToHostPath()
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "agent-data");
        AllowedDirectory[] allowedDirectories =
        [
            new(dataPath, "data", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/data")
        ];

        bool resolved = PathHelper.TryResolveToHostPath(
            "/data/notes.md",
            allowedDirectories,
            dataPath,
            out string hostPath);

        Assert.True(resolved);
        Assert.Equal(Path.GetFullPath(Path.Combine(dataPath, "notes.md")), hostPath);
    }

    [Fact]
    public void TryResolveToHostPath_MapsRelativePathAgainstWorkingDirectory()
    {
        string workspacePath = Path.Combine(Path.GetTempPath(), "workspace-default");

        bool resolved = PathHelper.TryResolveToHostPath(
            "out.txt",
            [],
            workspacePath,
            out string hostPath);

        Assert.True(resolved);
        Assert.Equal(Path.GetFullPath(Path.Combine(workspacePath, "out.txt")), hostPath);
    }

    [Fact]
    public void TryResolveToHostPath_MapsBareWorkspacePathAgainstWorkingDirectory()
    {
        string workspacePath = Path.Combine(Path.GetTempPath(), "workspace-default");

        bool resolved = PathHelper.TryResolveToHostPath(
            "/workspace/out.txt",
            [],
            workspacePath,
            out string hostPath);

        Assert.True(resolved);
        Assert.Equal(Path.GetFullPath(Path.Combine(workspacePath, "out.txt")), hostPath);
    }

    [Fact]
    public void TryResolveToHostPath_PassesThroughHostAbsolutePath()
    {
        string absolutePath = Path.Combine(Path.GetTempPath(), "workspace-default", "out.txt");

        bool resolved = PathHelper.TryResolveToHostPath(
            absolutePath,
            [],
            workingDirectoryHostPath: null,
            out string hostPath);

        Assert.True(resolved);
        Assert.Equal(Path.GetFullPath(absolutePath), hostPath);
    }

    [Fact]
    public void TryResolveToAgentPath_MapsHostPathToSandboxWorkspacePath()
    {
        string workspacePath = Path.Combine(Path.GetTempPath(), "workspace-default");
        string hostPath = Path.Combine(workspacePath, "releases", "app.zip");
        AllowedDirectory[] allowedDirectories =
        [
            new(workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/workspace/default")
        ];

        bool resolved = PathHelper.TryResolveToAgentPath(hostPath, allowedDirectories, out string agentPath);

        Assert.True(resolved);
        Assert.Equal("/workspace/default/releases/app.zip", agentPath);
    }

    [Fact]
    public void TryResolveToAgentPath_LocalModePreservesHostPath()
    {
        string workspacePath = Path.Combine(Path.GetTempPath(), "workspace-default");
        string hostPath = Path.Combine(workspacePath, "out.txt");
        AllowedDirectory[] allowedDirectories =
        [
            new(workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ];

        bool resolved = PathHelper.TryResolveToAgentPath(hostPath, allowedDirectories, out string agentPath);

        Assert.True(resolved);
        Assert.Equal(Path.GetFullPath(hostPath), agentPath);
    }

    [Fact]
    public void TryResolveToHostPath_OutsidePathResolvesButFailsContainment()
    {
        string workspacePath = Path.Combine(Path.GetTempPath(), "workspace-default");
        string outsidePath = Path.Combine(Path.GetTempPath(), "outside", "out.txt");
        AllowedDirectory[] allowedDirectories =
        [
            new(workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ];

        bool resolved = PathHelper.TryResolveToHostPath(
            outsidePath,
            allowedDirectories,
            workspacePath,
            out string hostPath);
        bool contained = PathHelper.TryGetContainingDirectory(hostPath, allowedDirectories, out _);

        Assert.True(resolved);
        Assert.False(contained);
    }

    [Fact]
    public void ResolveAuthorizedHostPath_ResolvesInsideGrantAndRejectsOutsideOrReadOnlyWrite()
    {
        string workspacePath = Path.Combine(Path.GetTempPath(), $"oscova-authorized-{Guid.NewGuid():N}");
        AllowedDirectory[] writableDirectories =
        [
            new(workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/workspace/default")
        ];

        string resolved = PathHelper.ResolveAuthorizedHostPath(
            "/workspace/default/notes.txt",
            writableDirectories,
            WorkspacePathAccess.Read);

        Assert.Equal(Path.GetFullPath(Path.Combine(workspacePath, "notes.txt")), resolved);
        Assert.Throws<UnauthorizedAccessException>(() => PathHelper.ResolveAuthorizedHostPath(
            Path.Combine(Path.GetTempPath(), "outside", "notes.txt"),
            writableDirectories,
            WorkspacePathAccess.Read));

        AllowedDirectory[] readOnlyDirectories =
        [
            new(workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly, AgentPath: "/workspace/default")
        ];
        Assert.Throws<UnauthorizedAccessException>(() => PathHelper.ResolveAuthorizedHostPath(
            "/workspace/default/notes.txt",
            readOnlyDirectories,
            WorkspacePathAccess.Write));
    }

    [SkippableFact]
    public void ResolveAuthorizedHostPath_RejectsSymlinkHop()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"oscova-authorized-link-{Guid.NewGuid():N}");
        string workspacePath = Path.Combine(tempRoot, "workspace");
        string outsidePath = Path.Combine(tempRoot, "outside");
        string linkPath = Path.Combine(workspacePath, "linked");

        try
        {
            Directory.CreateDirectory(workspacePath);
            Directory.CreateDirectory(outsidePath);
            try
            {
                Directory.CreateSymbolicLink(linkPath, outsidePath);
            }
            catch (Exception exception) when (exception is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
            {
                Skip.If(true, $"Symbolic link creation is unavailable on this runner: {exception.Message}");
            }

            AllowedDirectory[] allowedDirectories =
            [
                new(workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/workspace/default")
            ];

            Assert.Throws<UnauthorizedAccessException>(() => PathHelper.ResolveAuthorizedHostPath(
                "/workspace/default/linked/secret.txt",
                allowedDirectories,
                WorkspacePathAccess.Read));
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
