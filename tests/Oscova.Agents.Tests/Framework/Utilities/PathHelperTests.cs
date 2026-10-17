using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tests.Framework.Utilities;

public sealed class PathHelperTests
{
    /// <summary>Alias ownership remains lexical when native roots belong to another operating system.</summary>
    [Fact]
    public void GetAgentPathRoots_MatchesNormalizedLongestAliasAcrossPlatforms()
    {
        AllowedDirectory[] grants =
        [
            new("C:\\Users\\owner\\project", "project", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/project"),
            new("/srv/workspace", "workspace", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/workspace/default"),
            new("/srv/nested", "nested", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly, "/workspace/default/nested")
        ];
        Assert.Same(grants[0], Assert.Single(PathHelper.GetAgentPathRoots("\\project\\.\\record.txt", grants)));
        Assert.Same(grants[2], Assert.Single(PathHelper.GetAgentPathRoots("/workspace//default/nested/record.txt", grants)));
        Assert.Empty(PathHelper.GetAgentPathRoots("/workspace/default-other/record.txt", grants));
        Assert.Empty(PathHelper.GetAgentPathRoots("C:\\Users\\owner\\project\\record.txt", grants));
    }

    [Fact]
    public void ShellRootVariables_UsePortableUniqueNamesForEveryAliasedGrant()
    {
        var path = Path.GetTempPath();
        AllowedDirectory[] grants =
        [
            new(path, "private", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/private"),
            new(path, "research", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/workspace/research"),
            new(path, "other", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/workspace/RESEARCH"),
            new(path, "試験", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/workspace/試験")
        ];
        var variables = PathHelper.GetShellRootVariables(grants);
        Assert.Equal(grants.Length, variables.Count);
        Assert.Same(grants[0], variables["ROOT_PRIVATE"]);
        Assert.Same(grants[1], variables["ROOT_WORKSPACE_RESEARCH"]);
        Assert.Same(grants[2], variables["ROOT_EXTRA_2"]);
        Assert.Same(grants[3], variables["ROOT_EXTRA_3"]);
    }

    [Fact]
    public void WorkingDirectoryMarker_OverridesDefaultAndNeverFallsBackWhenUnavailable()
    {
        var root = Directory.CreateTempSubdirectory("oscova-working-root-");
        try
        {
            var project = new AllowedDirectory(root.FullName, "project", DirectorySourceKind.LocalPath,
                WorkspaceAccessMode.ReadWrite, IsWorkingDirectory: true);
            var fallback = project with { Value = Path.GetTempPath(), Label = "default", IsWorkingDirectory = false };
            Assert.Equal(project.Value, PathHelper.ResolveWorkingDirectoryHostPath([fallback, project]));
            Assert.Throws<UnauthorizedAccessException>(() => PathHelper.ResolveWorkingDirectoryHostPath(
                [fallback, project, project with { AccessMode = WorkspaceAccessMode.ReadOnly, IsWorkingDirectory = false }]));
            Assert.Throws<InvalidOperationException>(() => PathHelper.ResolveWorkingDirectoryHostPath([project, project]));
            root.Delete();
            Assert.Throws<UnauthorizedAccessException>(() => PathHelper.ResolveWorkingDirectoryHostPath([fallback, project]));
            Assert.False(Directory.Exists(root.FullName));
        }
        finally { if (Directory.Exists(root.FullName)) { root.Delete(true); } }
    }

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
            new(dataPath, "private", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/private")
        ];

        bool resolved = PathHelper.TryResolveToHostPath(
            "/private/notes.md",
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
    public void TryResolveToHostPath_RejectsUnknownWorkspaceRoot()
    {
        string workspacePath = Path.Combine(Path.GetTempPath(), "workspace-default");

        bool resolved = PathHelper.TryResolveToHostPath(
            "/workspace/out.txt",
            [],
            workspacePath,
            out string hostPath);

        Assert.False(resolved);
        Assert.Empty(hostPath);
    }

    [Theory]
    [InlineData("workspace/research/out.txt", "research", "out.txt")]
    [InlineData("./workspace/research/out.txt", "research", "out.txt")]
    [InlineData("/workspace//research/./drafts/out.txt", "research", "drafts/out.txt")]
    [InlineData("private/notes/out.txt", "private", "notes/out.txt")]
    [InlineData("/mnt/reference/out.txt", "reference", "out.txt")]
    public void TryResolveToHostPath_MapsLogicalAliasesInEveryMode(
        string requestedPath,
        string expectedLabel,
        string expectedRelativePath)
    {
        string root = Path.Combine(Path.GetTempPath(), "logical-routes");
        AllowedDirectory[] allowedDirectories =
        [
            new(Path.Combine(root, "default"), "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/workspace/default"),
            new(Path.Combine(root, "research"), "research", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/workspace/research"),
            new(Path.Combine(root, "private"), "private", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/private"),
            new(Path.Combine(root, "reference"), "reference", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly, AgentPath: "/mnt/reference")
        ];

        bool resolved = PathHelper.TryResolveToHostPath(
            requestedPath,
            allowedDirectories,
            allowedDirectories[0].Value,
            out string hostPath);

        Assert.True(resolved);
        AllowedDirectory expectedDirectory = Assert.Single(allowedDirectories, directory => directory.Label == expectedLabel);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(expectedDirectory.Value, PathHelper.ToPlatformRelativePath(expectedRelativePath))),
            hostPath);
    }

    [Theory]
    [InlineData("workspace/missing/out.txt")]
    [InlineData("workspace/research-copy/out.txt")]
    [InlineData("private/out.txt")]
    [InlineData("/private/../workspace/default/out.txt")]
    [InlineData("mnt/reference/../../out.txt")]
    public void TryResolveToHostPath_RejectsUnknownOrEscapingLogicalAliases(string requestedPath)
    {
        string workspacePath = Path.Combine(Path.GetTempPath(), "workspace-default");
        AllowedDirectory[] allowedDirectories =
        [
            new(workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/workspace/default"),
            new(Path.Combine(Path.GetTempPath(), "reference"), "reference", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly, AgentPath: "/mnt/reference")
        ];

        bool resolved = PathHelper.TryResolveToHostPath(
            requestedPath,
            allowedDirectories,
            workspacePath,
            out string hostPath);

        Assert.False(resolved);
        Assert.Empty(hostPath);
    }

    [SkippableTheory]
    [InlineData("/mnt/disk/out.txt", true)]
    [InlineData("/mnt/missing/out.txt", false)]
    public void TryResolveToHostPath_OnLinuxPreservesGrantedNativePathsUnderReservedRoots(string requestedPath, bool expected)
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Rooted logical aliases require native Linux path semantics.");
        AllowedDirectory[] allowedDirectories =
        [
            new("/mnt/disk", "archive", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/mnt/archive")
        ];

        bool resolved = PathHelper.TryResolveToHostPath(
            requestedPath,
            allowedDirectories,
            allowedDirectories[0].Value,
            out string hostPath);

        Assert.Equal(expected, resolved);
        Assert.Equal(expected ? requestedPath : string.Empty, hostPath);
        if (expected)
        {
            Assert.Equal(hostPath, PathHelper.ResolveAuthorizedHostPath(
                "mnt/archive/out.txt", allowedDirectories, WorkspacePathAccess.Read));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TryResolveToHostPath_RejectsAmbiguousAliasesRegardlessOfOrder(bool reverse, bool sameRoot)
    {
        string root = Path.Combine(Path.GetTempPath(), "alias-conflict");
        string first = Path.Combine(root, "first");
        AllowedDirectory[] directories =
        [
            new(first, "first", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/mnt/reference"),
            new(sameRoot ? first : Path.Combine(root, "second"), "second", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly, AgentPath: "/mnt/reference")
        ];
        if (reverse) Array.Reverse(directories);

        bool resolved = PathHelper.TryResolveToHostPath("mnt/reference/out.txt", directories, first, out string hostPath);

        Assert.Equal(sameRoot, resolved);
        Assert.Equal(sameRoot ? Path.Combine(first, "out.txt") : string.Empty, hostPath);
        if (sameRoot)
        {
            Assert.Throws<UnauthorizedAccessException>(() => PathHelper.ResolveAuthorizedHostPath(
                "mnt/reference/out.txt", directories, WorkspacePathAccess.Write));
        }
    }

    [Fact]
    public void TryResolveToHostPath_UsesTheMostSpecificLogicalAlias()
    {
        string root = Path.Combine(Path.GetTempPath(), "logical-routes");
        string reportsPath = Path.Combine(root, "reports");
        AllowedDirectory[] allowedDirectories =
        [
            new(Path.Combine(root, "archive"), "archive", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/mnt/archive"),
            new(reportsPath, "reports", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/mnt/archive/reports")
        ];

        bool resolved = PathHelper.TryResolveToHostPath(
            "mnt/archive/reports/summary.txt",
            allowedDirectories,
            allowedDirectories[0].Value,
            out string hostPath);

        Assert.True(resolved);
        Assert.Equal(Path.GetFullPath(Path.Combine(reportsPath, "summary.txt")), hostPath);
    }

    [Fact]
    public void ResolveWorkingDirectoryHostPath_UsesFirstWritableGrantOrReturnsNull()
    {
        AllowedDirectory readOnly = new("readonly", "library", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly);
        AllowedDirectory writable = new("writable", "research", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite);

        Assert.Equal(writable.Value, PathHelper.ResolveWorkingDirectoryHostPath([readOnly, writable]));
        Assert.Null(PathHelper.ResolveWorkingDirectoryHostPath([readOnly]));
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
    public void TryResolveToAgentPath_CanonicalAndNativePathsReachTheSameFile()
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
        Assert.Equal(hostPath, PathHelper.ResolveAuthorizedHostPath(agentPath, allowedDirectories, WorkspacePathAccess.Read));
        Assert.Equal(hostPath, PathHelper.ResolveAuthorizedHostPath(hostPath, allowedDirectories, WorkspacePathAccess.Read));
    }

    [Fact]
    public void TryResolveToAgentPath_NativeOnlyGrantPreservesHostPath()
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

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveAuthorizedHostPath_RejectsSymlinkHop(bool useFilesystemRoot)
    {
        string tempDirectory = Path.GetFullPath(Path.GetTempPath());
        // Resolve linked temp ancestors, such as /var on macOS, so the plain-path control is link-free.
        for (var ancestor = new DirectoryInfo(tempDirectory); ancestor.Parent is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                tempDirectory = Path.Combine(target.FullName, Path.GetRelativePath(ancestor.FullName, tempDirectory));
            }
        }

        string tempRoot = Path.Combine(tempDirectory, $"oscova-authorized-link-{Guid.NewGuid():N}");
        string workspacePath = Path.Combine(tempRoot, "workspace");
        string outsidePath = Path.Combine(tempRoot, "outside");
        string linkPath = Path.Combine(workspacePath, "linked");
        string grantRoot = useFilesystemRoot ? Path.GetPathRoot(workspacePath)! : workspacePath;

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
                new(grantRoot, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/workspace/default")
            ];

            string plainPath = Path.Combine(workspacePath, "plain.txt");
            Assert.False(PathHelper.HasSymlinkInPath(plainPath, grantRoot));
            Assert.Equal(plainPath, PathHelper.ResolveAuthorizedHostPath(plainPath, allowedDirectories, WorkspacePathAccess.Read));

            string linkedPath = Path.Combine(linkPath, "secret.txt");
            Assert.True(PathHelper.HasSymlinkInPath(linkedPath, grantRoot));
            Assert.Throws<UnauthorizedAccessException>(() => PathHelper.ResolveAuthorizedHostPath(
                useFilesystemRoot ? linkedPath : "/workspace/default/linked/secret.txt",
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
