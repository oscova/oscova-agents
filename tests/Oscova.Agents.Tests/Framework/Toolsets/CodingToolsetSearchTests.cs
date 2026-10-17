using Oscova.Agents.Harness;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tests.Framework.Toolsets;

public sealed class CodingToolsetSearchTests : IDisposable
{
    private readonly string _workspacePath;

    public CodingToolsetSearchTests()
    {
        _workspacePath = Path.Combine(
            Environment.CurrentDirectory,
            "search-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspacePath);
    }

    /// <summary>Search results use aliases while native inputs still open the same bytes.</summary>
    [Fact]
    public void Search_NativeInputs_ReturnCanonicalAliases()
    {
        string file = Path.Combine(_workspacePath, "record.txt");
        File.WriteAllText(file, "needle");
        var grants = new AllowedDirectory[]
        {
            new(_workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/workspace/default", true)
        };
        var toolset = new CodingToolset(() => grants);

        Assert.Contains("/workspace/default/record.txt", toolset.Glob("*.txt"), StringComparison.Ordinal);
        Assert.Contains("/workspace/default/record.txt", toolset.Grep("needle"), StringComparison.Ordinal);
        Assert.Contains("needle", toolset.ReadFile("/workspace/default/record.txt"), StringComparison.Ordinal);
    }

    /// <summary>An empty filename search identifies its actual scope without widening it.</summary>
    [Fact]
    public void Glob_NoMatches_ReportsCanonicalSearchRoot()
    {
        var toolset = CreateSandboxToolset();
        Assert.Equal("No files matched under /workspace/default.", toolset.Glob("missing.json"));
        Directory.CreateDirectory(Path.Combine(_workspacePath, "nested"));
        Assert.Equal("No files matched under /workspace/default/nested.",
            toolset.Glob("missing.json", "/workspace/default/nested"));
    }

    [Fact]
    public void Grep_ReportsTrueTotalAndSavesFullResults()
    {
        File.WriteAllText(Path.Combine(_workspacePath, "many.txt"), string.Join('\n', Enumerable.Repeat("needle", 105)));
        var store = new RecordingToolOutputStore();
        var toolset = CreateSandboxToolset(store: store);

        string result = toolset.Grep("needle", output_mode: "content", limit: 2);

        Assert.Contains("Showing 2 of 105 results. Full results saved", result);
        Assert.Contains(":105: needle", store.Saved);
        Assert.DoesNotContain(":105: needle", result);
    }

    [Fact]
    public void Grep_OmitsPointerWhenStoreReturnsNull()
    {
        File.WriteAllText(Path.Combine(_workspacePath, "many.txt"), "needle\nneedle\nneedle");
        var store = new RecordingToolOutputStore { Path = null };

        string result = CreateSandboxToolset(store: store).Grep("needle", output_mode: "content", limit: 1);

        Assert.Contains("[Showing 1 of 3 results.]", result);
        Assert.DoesNotContain("saved to", result);
    }

    [Fact]
    public void Glob_ReportsTrueTotalPastLimit()
    {
        for (int index = 0; index < 205; index++)
        {
            File.WriteAllText(Path.Combine(_workspacePath, $"file-{index:D3}.txt"), string.Empty);
        }

        var store = new RecordingToolOutputStore();
        string result = CreateSandboxToolset(store: store).Glob("*.txt");

        Assert.Contains("Showing 200 of 205 results", result);
        Assert.Equal(205, store.Saved!.Split(Environment.NewLine).Length);
    }

    [Fact]
    public void Grep_KeepsInlineResultsWhenOneEntryExceedsTheSaveCeiling()
    {
        string huge = new('x', BoundedOutputBuffer.DefaultCapacityBytes + 1);
        File.WriteAllText(Path.Combine(_workspacePath, "large.txt"), huge + "needle\nneedle");
        var store = new RecordingToolOutputStore();

        string result = CreateSandboxToolset(store: store).Grep("needle", output_mode: "content", limit: 1);

        Assert.Contains(huge, result);
        Assert.Contains("Showing 1 of 2 results", result);
    }

    [Fact]
    public void Grep_KeepsSavedPayloadWithinTheCeilingWhenAnInlineEntryExceedsIt()
    {
        string huge = new('x', BoundedOutputBuffer.DefaultCapacityBytes + 1);
        File.WriteAllText(Path.Combine(_workspacePath, "large.txt"), $"needle\n{huge}needle\nneedle");
        var store = new RecordingToolOutputStore();

        string result = CreateSandboxToolset(store: store).Grep("needle", output_mode: "content", limit: 2);

        Assert.Contains(huge, result);
        Assert.Contains("Showing 2 of 3 results. First 1 results saved", result);
        Assert.DoesNotContain(huge, store.Saved);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(store.Saved!) <= BoundedOutputBuffer.DefaultCapacityBytes);
    }

    [Fact]
    public void Grep_OmitsPointerWhenNothingFitsTheCeiling()
    {
        string huge = new('x', BoundedOutputBuffer.DefaultCapacityBytes + 1);
        File.WriteAllText(Path.Combine(_workspacePath, "large.txt"), $"{huge}needle\nneedle");
        var store = new RecordingToolOutputStore();

        string result = CreateSandboxToolset(store: store).Grep("needle", output_mode: "content", limit: 1);

        Assert.Contains(huge, result);
        Assert.Contains("[Showing 1 of 2 results.]", result);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public void Grep_MeasuresSaveCeilingInUtf8Bytes()
    {
        File.WriteAllText(Path.Combine(_workspacePath, "unicode.txt"), string.Join('\n', Enumerable.Repeat("needle" + new string('界', 1000), 500)));
        var store = new RecordingToolOutputStore();

        string result = CreateSandboxToolset(store: store).Grep("needle", output_mode: "content", limit: 1);

        Assert.Contains("First ", result);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(store.Saved!) <= BoundedOutputBuffer.DefaultCapacityBytes);
    }

    [Fact]
    public void Glob_MeasuresSaveCeilingInUtf8Bytes()
    {
        var entries = Enumerable.Range(0, 4000)
            .Select(index => new WorkspaceFileEntry(
                Path.Combine(_workspacePath, $"{index:D4}-{new string('界', 100)}.txt"),
                FileAttributes.Normal, 0, DateTime.UtcNow))
            .ToArray();
        var grants = new[]
        {
            new AllowedDirectory(_workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite,
                AgentPath: "/workspace/default", IsWorkingDirectory: true)
        };
        var store = new RecordingToolOutputStore();
        var toolset = new CodingToolset(() => grants, new GeneratedFileAccess(_workspacePath, entries), store);

        string result = toolset.Glob("*.txt");

        Assert.Contains("Showing 200 of 4000 results. First ", result);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(store.Saved!) <= BoundedOutputBuffer.DefaultCapacityBytes);
        Assert.Contains("界", store.Saved);
    }

    [Fact]
    public void Grep_TreatsEncFileAsPlainFile()
    {
        File.WriteAllText(Path.Combine(_workspacePath, "notes.md.enc"), "v1.Q2lwaGVy");

        string result = CreateSandboxToolset().Grep("Q2lwaGVy", output_mode: "content");

        Assert.Contains("notes.md.enc:1: v1.Q2lwaGVy", result);
        Assert.DoesNotContain("vault", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("encrypt", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grep_ResolvesRelativePathAgainstWorkspace()
    {
        File.WriteAllText(
            Path.Combine(_workspacePath, "data.txt"),
            "alpha\nNEEDLE-CODE: FOUND-IT\nbeta\n");

        var toolset = new CodingToolset(() =>
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
        var toolset = new CodingToolset(() => grants, files);

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

    private CodingToolset CreateSandboxToolset(string? workspacePath = null, IToolOutputStore? store = null)
    {
        return new CodingToolset(() =>
        [
            new AllowedDirectory(
                workspacePath ?? _workspacePath,
                "default",
                DirectorySourceKind.LocalPath,
                WorkspaceAccessMode.ReadWrite,
                AgentPath: "/workspace/default")
        ], outputStore: store);
    }

    private sealed class GeneratedFileAccess(string root, IReadOnlyList<WorkspaceFileEntry> entries) : IWorkspaceFileAccess
    {
        public Stream OpenRead(string path) => throw new NotSupportedException();
        public void Write(string path, Action<Stream> write, bool append = false) => throw new NotSupportedException();
        public void Delete(string path) => throw new NotSupportedException();
        public WorkspaceFileEntry? GetEntry(string path) => path == root
            ? new WorkspaceFileEntry(root, FileAttributes.Directory, 0, DateTime.UtcNow)
            : null;
        public IEnumerable<WorkspaceFileEntry> Enumerate(string directory) => directory == root ? entries : [];
    }
}
