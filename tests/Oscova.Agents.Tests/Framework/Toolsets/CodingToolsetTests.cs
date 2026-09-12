using Oscova.Agents.Harness;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tests.Framework.Toolsets;

public sealed class CodingToolsetTests : IDisposable
{
    private readonly string _workspacePath;
    private readonly CodingToolset _toolset = new();

    public CodingToolsetTests()
    {
        _workspacePath = Path.Combine(
            Environment.CurrentDirectory,
            "apply-patch-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspacePath);
    }

    /// <summary>A successful case-only move must preserve destination content on Windows.</summary>
    [SkippableFact]
    public void ApplyPatch_CaseOnlyMove_PreservesDestinationContent()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Requires Windows path semantics.");
        WriteWorkspaceFile("notes.txt", "before\n");
        var target = GetWorkspaceFile("Notes.txt");
        Skip.IfNot(File.Exists(target), "Requires a case-insensitive directory.");
        var result = _toolset.ApplyPatch("""
            *** Begin Patch
            *** Update File: notes.txt
            *** Move to: Notes.txt
            @@
            -before
            +after
            *** End Patch
            """, _workspacePath);

        Assert.Contains("moved 1 file(s)", result, StringComparison.Ordinal);
        Assert.True(File.Exists(target), $"Patch reported success but destination disappeared: {result}");
        Assert.Equal("after", File.ReadAllText(target).Trim());
    }

    [Fact]
    public void ApplyPatch_Applies_MultiFile_Operations()
    {
        var existingPath = WriteWorkspaceFile("src/Existing.cs", "namespace Example;\npublic class Existing\n{\n    public int Count;\n}");
        var obsoletePath = WriteWorkspaceFile("src/Obsolete.cs", "old");

        var patch = $$"""
            *** Begin Patch
            *** Add File: src/NewFile.cs
            +namespace Example;
            +public class NewFile { }
            *** Update File: src/Existing.cs
            *** Move to: src/Renamed.cs
            @@ public class Existing
             public class Existing
             {
            -    public int Count;
            +    public int Count = 0;
            +    public string Name = "Renamed";
             }
            *** Delete File: src/Obsolete.cs
            *** End Patch
            """;

        var summary = _toolset.ApplyPatch(patch, _workspacePath);

        var renamedPath = GetWorkspaceFile("src/Renamed.cs");
        var newFilePath = GetWorkspaceFile("src/NewFile.cs");

        Assert.Equal("Updated 1 file(s), added 1 file(s), deleted 1 file(s), moved 1 file(s).", summary);
        Assert.False(File.Exists(existingPath));
        Assert.False(File.Exists(obsoletePath));
        Assert.True(File.Exists(renamedPath));
        Assert.True(File.Exists(newFilePath));
        Assert.Contains("public int Count = 0;", File.ReadAllText(renamedPath));
        Assert.Contains("public string Name = \"Renamed\";", File.ReadAllText(renamedPath));
        Assert.Contains("public class NewFile", File.ReadAllText(newFilePath));
    }

    [Fact]
    public void ApplyPatch_WhenHunkIsMissing_Throws_AndLeavesFileUnchanged()
    {
        var filePath = WriteWorkspaceFile("src/Missing.cs", "alpha\nbeta\ngamma\n");
        var originalContent = File.ReadAllText(filePath);

        var patch = $$"""
            *** Begin Patch
            *** Update File: src/Missing.cs
            @@ missing
            -delta
            +epsilon
            *** End Patch
            """;

        var exception = Assert.Throws<ToolFailureException>(() => _toolset.ApplyPatch(patch, _workspacePath));

        Assert.Equal("A patch section could not be located.", exception.Message);
        Assert.Equal(originalContent, File.ReadAllText(filePath));
    }

    [Fact]
    public void ApplyPatch_WhenHunkMatchesMultipleLocations_Throws_AndLeavesFileUnchanged()
    {
        var filePath = WriteWorkspaceFile("src/Ambiguous.cs", "start\nrepeat\nvalue\nend\nrepeat\nvalue\n");
        var originalContent = File.ReadAllText(filePath);

        var patch = $$"""
            *** Begin Patch
            *** Update File: src/Ambiguous.cs
            @@ duplicate
             repeat
            -value
            +updated
            *** End Patch
            """;

        var exception = Assert.Throws<ToolFailureException>(() => _toolset.ApplyPatch(patch, _workspacePath));

        Assert.Equal("A patch section matched multiple locations.", exception.Message);
        Assert.Equal(originalContent, File.ReadAllText(filePath));
    }

    [Fact]
    public void ApplyPatch_WhenTargetIsInNestedReadOnlyDirectory_ThrowsAndLeavesFileUnchanged()
    {
        string filePath = WriteWorkspaceFile("protected/secret.txt", "original");
        string protectedPath = Path.GetDirectoryName(filePath)!;
        IReadOnlyList<AllowedDirectory> allowedDirectories =
        [
            new AllowedDirectory(_workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite),
            new AllowedDirectory(protectedPath, "protected", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadOnly)
        ];
        var toolset = new CodingToolset(() => allowedDirectories);
        const string patch = """
            *** Begin Patch
            *** Update File: protected/secret.txt
            @@
            -original
            +changed
            *** End Patch
            """;

        var exception = Assert.Throws<ToolFailureException>(() => toolset.ApplyPatch(patch, _workspacePath));
        Assert.Equal("The patch path is read-only.", exception.Message);
        Assert.DoesNotContain(filePath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original", File.ReadAllText(filePath));
    }

    [Theory]
    [InlineData("alpha", "missing", "The text to replace was not found.")]
    [InlineData("alpha alpha", "alpha", "The text to replace is not unique.")]
    public void EditFile_WhenTargetCannotBeSelected_ThrowsAndLeavesFileUnchanged(
        string content,
        string target,
        string reason)
    {
        var path = WriteWorkspaceFile("edit/notes.txt", content);

        var exception = Assert.Throws<ToolFailureException>(() => _toolset.EditFile(path, target, "updated"));

        Assert.Equal(reason, exception.Message);
        Assert.DoesNotContain(path, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void WriteAppendAndEdit_PreserveExistingFileContract()
    {
        var path = GetWorkspaceFile("write/notes.txt");

        Assert.Equal($"Wrote 5 characters to '{path}'.", _toolset.WriteFile(path, "alpha"));
        Assert.Equal($"Appended 5 characters to '{path}'.", _toolset.WriteFile(path, " beta", append: true));
        Assert.Equal($"Replaced 1 occurrence(s) in '{path}'.", _toolset.EditFile(path, "beta", "bravo"));

        Assert.Equal("alpha bravo", File.ReadAllText(path));
    }

    [Fact]
    public void ReadFile_CapsAnOverLongLine_AndReportsSize()
    {
        var path = WriteWorkspaceFile("read/oneline.txt", new string('a', 5000));

        var result = _toolset.ReadFile(path);

        // One 5000-char line is capped at 2000, leaving a marker for the 3000 dropped characters.
        Assert.Contains("[+3000 chars truncated]", result);
        Assert.DoesNotContain(new string('a', 2500), result);
        Assert.Contains("bytes)", result);
    }

    [Fact]
    public void ReadFile_StopsAtPageBudget_AndEmitsContinuation()
    {
        var content = string.Join("\n", Enumerable.Repeat(new string('x', 1000), 100));
        var path = WriteWorkspaceFile("read/many.txt", content);

        var result = _toolset.ReadFile(path);

        Assert.Contains("Page stopped at the 50000-character budget", result);
        Assert.Contains("Continue with offset=", result);
        Assert.Contains("of 100", result);
    }

    [Fact]
    public void ReadFile_ReachedLineLimit_EmitsContinuation()
    {
        var content = string.Join("\n", Enumerable.Range(1, 10).Select(number => $"line {number}"));
        var path = WriteWorkspaceFile("read/lines.txt", content);

        var result = _toolset.ReadFile(path, offset: 1, limit: 3);

        Assert.Contains("Reached the 3-line page limit. Continue with offset=4 of 10.", result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
    }

    private string WriteWorkspaceFile(string relativePath, string content)
    {
        var fullPath = GetWorkspaceFile(relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
        return fullPath;
    }

    private string GetWorkspaceFile(string relativePath)
    {
        return Path.Combine(_workspacePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
