using Oscova.Agents.Harness;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tests.Framework.Toolsets;

public sealed class SearchToolsetTests : IDisposable
{
    private readonly string _workspacePath;

    public SearchToolsetTests()
    {
        _workspacePath = Path.Combine(
            Environment.CurrentDirectory,
            "search-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspacePath);
    }

    [Fact]
    public void Grep_ResolvesRelativePathAgainstWorkspace()
    {
        File.WriteAllText(
            Path.Combine(_workspacePath, "data.txt"),
            "alpha\nNEEDLE-CODE: FOUND-IT\nbeta\n");

        var toolset = new SearchToolset(() =>
        [
            new AllowedDirectory(_workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ]);

        // A relative path must resolve against the workspace, not the host process working directory.
        var result = toolset.Grep("NEEDLE-CODE", path: "data.txt", output_mode: "content");

        Assert.Contains("FOUND-IT", result);
    }

    [Fact]
    public void Grep_SandboxModeReturnsAgentFacingPath()
    {
        var hostPath = Path.Combine(_workspacePath, "notes", "release.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(hostPath)!);
        File.WriteAllText(hostPath, "release candidate");
        var toolset = CreateSandboxToolset();

        var result = toolset.Grep("candidate", path: "/workspace/default", output_mode: "content");

        Assert.Contains("/workspace/default/notes/release.txt:1:", result);
        Assert.DoesNotContain(_workspacePath, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Glob_SandboxModeReturnsAgentFacingPath()
    {
        var hostPath = Path.Combine(_workspacePath, "releases", "app.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(hostPath)!);
        File.WriteAllText(hostPath, "package");
        var toolset = CreateSandboxToolset();

        var result = toolset.Glob("**/*.zip", "/workspace/default");

        Assert.Contains("/workspace/default/releases/app.zip", result);
        Assert.DoesNotContain(_workspacePath, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FilteredSearch_PrunesExcludedDirectoriesButStillTraversesMatchingSubtrees()
    {
        string excluded = Path.Combine(_workspacePath, "excluded");
        string included = Path.Combine(_workspacePath, "included", "nested");
        Directory.CreateDirectory(excluded);
        Directory.CreateDirectory(included);
        File.WriteAllText(Path.Combine(_workspacePath, "wanted.txt"), "needle");
        File.WriteAllText(Path.Combine(included, "wanted.cs"), "needle");
        IReadOnlyList<AllowedDirectory> grants =
        [
            new(_workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ];
        var files = new DeniedDirectoryAccess(new WorkspaceFileAccess(() => grants), excluded);
        var toolset = new SearchToolset(() => grants, files);

        Assert.Contains("wanted.txt", toolset.Glob("*.txt", _workspacePath), StringComparison.Ordinal);
        Assert.Contains("wanted.txt", toolset.Grep("needle", _workspacePath, include: "*.txt"), StringComparison.Ordinal);
        Assert.Contains("wanted.cs", toolset.Glob("included/**/*.cs", _workspacePath), StringComparison.Ordinal);
        Assert.Contains("wanted.cs", toolset.Grep("needle", _workspacePath, include: "included/**/*.cs"), StringComparison.Ordinal);
        Assert.Throws<ToolFailureException>(() => toolset.Glob("**/*.txt", _workspacePath));
    }

    [SkippableFact]
    public void FilteredSearch_ReportsLinkNamesWithoutReadingTheirTargets()
    {
        string target = Path.Combine(_workspacePath, "target.txt");
        File.WriteAllText(target, "needle");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_workspacePath, "alias.txt"), target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Skip.If(true, $"File links are unavailable: {exception.Message}");
        }

        var toolset = CreateSandboxToolset();
        Assert.Contains("alias.txt", toolset.Glob("*.txt"), StringComparison.Ordinal);
        string matches = toolset.Grep("needle", include: "*.txt");
        Assert.Contains("target.txt", matches, StringComparison.Ordinal);
        Assert.DoesNotContain("alias.txt", matches, StringComparison.Ordinal);
    }

    /// <summary>Injects only denied enumeration while retaining real file access and metadata.</summary>
    private sealed class DeniedDirectoryAccess(IWorkspaceFileAccess inner, string denied) : IWorkspaceFileAccess
    {
        public Stream OpenRead(string path) => inner.OpenRead(path);
        public void Write(string path, Action<Stream> write, bool append = false) => inner.Write(path, write, append);
        public void Delete(string path) => inner.Delete(path);
        public WorkspaceFileEntry? GetEntry(string path) => inner.GetEntry(path);
        public IEnumerable<WorkspaceFileEntry> Enumerate(string directory) => directory == denied
            ? throw new UnauthorizedAccessException("Directory enumeration denied.")
            : inner.Enumerate(directory);
    }

    [Fact]
    public void Glob_WithMissingDefaultWorkspaceReportsAgentFacingPath()
    {
        var missingWorkspacePath = Path.Combine(_workspacePath, "missing-glob");
        var toolset = CreateSandboxToolset(missingWorkspacePath);

        var exception = Assert.Throws<ToolFailureException>(() => toolset.Glob("**/*.zip"));

        Assert.Equal("The search directory was not found.", exception.Message);
        Assert.DoesNotContain("/workspace/default", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(missingWorkspacePath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grep_WithMissingDefaultWorkspaceReportsAgentFacingPath()
    {
        var missingWorkspacePath = Path.Combine(_workspacePath, "missing-grep");
        var toolset = CreateSandboxToolset(missingWorkspacePath);

        var exception = Assert.Throws<ToolFailureException>(() => toolset.Grep("candidate"));

        Assert.Equal("The search path was not found.", exception.Message);
        Assert.DoesNotContain("/workspace/default", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(missingWorkspacePath, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
    }

    private SearchToolset CreateSandboxToolset(string? workspacePath = null)
    {
        return new SearchToolset(() =>
        [
            new AllowedDirectory(
                workspacePath ?? _workspacePath,
                "default",
                DirectorySourceKind.LocalPath,
                WorkspaceAccessMode.ReadWrite,
                AgentPath: "/workspace/default")
        ]);
    }
}
