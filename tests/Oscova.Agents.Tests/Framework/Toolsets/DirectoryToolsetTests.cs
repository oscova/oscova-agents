using Oscova.Agents.Harness;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tests.Framework.Toolsets;

public sealed class DirectoryToolsetTests
{
    /// <summary>A page remains directly usable when its ancestor entries are absent.</summary>
    [Fact]
    public void ListDirectory_PageWithoutAncestors_ReturnsUsableCompletePaths()
    {
        string root = Path.Combine(Path.GetTempPath(), $"oscova-directory-page-{Guid.NewGuid():N}");
        string file = Path.Combine(root, "docs", "operations", "regions", "north", "review.md");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "north review");
            var grants = new AllowedDirectory[]
            {
                new(root, "project", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, "/project", true)
            };
            var toolset = new DirectoryToolset(() => grants);
            string page = toolset.ListDirectory("/project", offset: 5, limit: 1, depth: 4);

            string expected = "/project/docs/operations/regions/north/review.md";
            Assert.Contains($"[FILE] {expected} (", page, StringComparison.Ordinal);
            string returnedPath = page.Split("[FILE] ")[1].Split(" (")[0];
            Assert.Contains("north review", new CodingToolset(() => grants).ReadFile(returnedPath), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Fact]
    public void ListDirectory_WhenDirectoryIsMissing_ThrowsSafeToolFailure()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var toolset = new DirectoryToolset(() =>
        [
            new(Path.GetTempPath(), "temp", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly)
        ]);

        var failure = Assert.Throws<ToolFailureException>(() => toolset.ListDirectory(missingPath));

        Assert.Equal("The requested directory was not found.", failure.Message);
        Assert.DoesNotContain(missingPath, failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void ListDirectory_WhenChildDirectoryIsSymlink_DoesNotTraverseOutsideWorkspace()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"oscova-directory-toolset-{Guid.NewGuid():N}");
        string workspace = Path.Combine(tempRoot, "workspace");
        string outside = Path.Combine(tempRoot, "outside");
        string link = Path.Combine(workspace, "linked-outside");

        try
        {
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "secret-marker.txt"), "secret");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
            {
                Skip.If(true, $"Symbolic link creation is unavailable on this runner: {ex.Message}");
            }

            IReadOnlyList<AllowedDirectory> allowedDirectories =
            [
                new AllowedDirectory(workspace, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
            ];
            var toolset = new DirectoryToolset(() => allowedDirectories);
            string result = toolset.ListDirectory(workspace, limit: 100, depth: 3);

            Assert.Contains("linked-outside", result, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-marker.txt", result, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
