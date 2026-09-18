using Oscova.Agents.Harness;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tests.Framework.Toolsets;

/// <summary>Checks workspace containment when a directory changes after path authorization.</summary>
public sealed class CodingToolsetPathContainmentTests
{
    /// <summary>An authorized file write must not follow a subsequently replaced parent outside its grant.</summary>
    [SkippableFact]
    public void WriteFile_WhenAValidatedParentIsRelinkedBeforeTheWrite_StaysInsideTheGrant()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Requires the Linux filesystem validation environment.");
        string root = Path.Combine(Path.GetTempPath(), "path-containment-tests", Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, "workspace");
        string parent = Path.Combine(workspace, "parent");
        string originalParent = Path.Combine(workspace, "original-parent");
        string outside = Path.Combine(root, "outside-grant");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        IReadOnlyList<AllowedDirectory> grants =
        [
            new(workspace, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ];
        var toolset = new CodingToolset(() => grants);
        string target = Path.Combine(parent, "out.txt");

        try
        {
            // Middleware authorizes the pathname before the tool performs its filesystem operations.
            Assert.Equal(target, PathHelper.ResolveAuthorizedHostPath(target, grants, WorkspacePathAccess.Write));

            // A background workspace writer can finish these operations while the host call is suspended.
            Directory.Move(parent, originalParent);
            Directory.CreateSymbolicLink(parent, outside);
            Assert.Throws<UnauthorizedAccessException>(() =>
                PathHelper.ResolveAuthorizedHostPath(target, grants, WorkspacePathAccess.Write));

            Exception? failure = Record.Exception(() => toolset.WriteFile(target, "contained-write-sentinel"));
            Assert.True(failure is null or ToolFailureException, $"Unexpected write failure: {failure}");
            string[] escapedFiles = Directory.GetFiles(outside);
            Assert.True(escapedFiles.Length == 0,
                $"Host write escaped the grant: {string.Join(", ", escapedFiles)}. Tool failure: {failure?.Message ?? "none"}");
        }
        finally
        {
            // Remove the link before recursively cleaning the test-owned directory tree.
            if (new DirectoryInfo(parent).LinkTarget is not null) Directory.Delete(parent);
            Directory.Delete(root, recursive: true);
        }
    }
}
