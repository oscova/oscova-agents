using Oscova.Agents.Collections;
using Oscova.Agents.Harness;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Tests.Framework.Toolsets;

public sealed class CodingToolsetTests : IDisposable
{
    private readonly string _workspacePath;
    private readonly CodingToolset _toolset;

    public CodingToolsetTests()
    {
        _workspacePath = Path.Combine(
            Environment.CurrentDirectory,
            "apply-patch-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspacePath);
        _toolset = new CodingToolset(() =>
        [
            new(_workspacePath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite)
        ]);
    }

    /// <summary>The registered file tools expose concise names under their File owner.</summary>
    [Fact]
    public void RegisteredTools_UseFileOwnerAndConciseNames()
    {
        var tools = new AgentToolCollection();
        tools.Add(_toolset);

        Assert.Equal(["file__apply_patch", "file__edit", "file__glob", "file__grep", "file__read", "file__write"],
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
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

        var patch = """
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
    public void ApplyPatch_WhenFormatIsInvalid_ExplainsAcceptedFormatAndLeavesFileUnchanged()
    {
        var filePath = WriteWorkspaceFile("notes.txt", "original");
        var tools = new AgentToolCollection();
        tools.Add(_toolset);
        var function = Assert.Single(
            tools,
            tool => tool.UnderlyingMethod?.Name == nameof(CodingToolset.ApplyPatch));
        var description = function.JsonSchema.GetProperty("properties").GetProperty("patch")
            .GetProperty("description").GetString()!.ReplaceLineEndings("\n");

        Assert.Contains("*** Begin Patch", description, StringComparison.Ordinal);
        Assert.Contains("*** Update File: path\n@@\n-old line\n+new line", description, StringComparison.Ordinal);
        Assert.Contains("*** Add File: path", description, StringComparison.Ordinal);
        Assert.Contains("*** Delete File: path", description, StringComparison.Ordinal);

        var exception = Assert.Throws<ToolFailureException>(() => _toolset.ApplyPatch(
            "diff --git a/notes.txt b/notes.txt",
            _workspacePath));

        Assert.Equal("The patch format is invalid.", exception.Message);
        var correction = Assert.IsType<string>(exception.Fix);
        Assert.Contains("Start with *** Begin Patch.", correction, StringComparison.Ordinal);
        Assert.Contains("End with *** End Patch.", correction, StringComparison.Ordinal);
        Assert.DoesNotContain(filePath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original", File.ReadAllText(filePath));

        _toolset.ApplyPatch("""
            *** Begin Patch
            *** Update File: notes.txt
            @@
            -original
            +updated
            *** End Patch
            """, _workspacePath);

        Assert.Equal("updated", File.ReadAllText(filePath));
    }

    [Fact]
    public void ApplyPatch_WhenHunkIsMissing_Throws_AndLeavesFileUnchanged()
    {
        var filePath = WriteWorkspaceFile("src/Missing.cs", "alpha\nbeta\ngamma\n");
        var originalContent = File.ReadAllText(filePath);

        var patch = """
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

        var patch = """
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
    public void ApplyPatch_WhenHeaderNamesALine_UpdatesTheMatchBelowIt()
    {
        var filePath = WriteWorkspaceFile("src/Twice.cs", "void First()\n{\n    return;\n}\nvoid Second()\n{\n    return;\n}\n");

        var patch = """
            *** Begin Patch
            *** Update File: src/Twice.cs
            @@ void Second()
             {
            -    return;
            +    Save();
            *** End Patch
            """;

        _toolset.ApplyPatch(patch, _workspacePath);

        Assert.Equal("void First()\n{\n    return;\n}\nvoid Second()\n{\n    Save();\n}\n", File.ReadAllText(filePath).ReplaceLineEndings("\n"));
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
        Assert.Equal("The patch path is not within a writable granted folder.", exception.Message);
        Assert.DoesNotContain(filePath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original", File.ReadAllText(filePath));
    }

    [Fact]
    public void ApplyPatch_WhenNoWorkspaceDirectoriesAreAllowed_ThrowsBeforeCreatingFile()
    {
        var targetPath = GetWorkspaceFile("denied.txt");
        var toolset = new CodingToolset(() => []);
        const string patch = """
            *** Begin Patch
            *** Add File: denied.txt
            +blocked
            *** End Patch
            """;

        var exception = Assert.Throws<ToolFailureException>(() => toolset.ApplyPatch(patch, _workspacePath));

        Assert.Equal("The patch path is not within a writable granted folder.", exception.Message);
        Assert.False(File.Exists(targetPath));
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
    public void EditFile_WhenCrlfFileGetsLfText_MatchesAndKeepsCrlf()
    {
        var path = GetWorkspaceFile("edit/crlf.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "one\r\ntwo\r\nthree\r\n");

        _toolset.EditFile(path, "one\ntwo", "uno\ndos");

        Assert.Equal("uno\r\ndos\r\nthree\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void EditFile_WhenMixedFileHasAnExactMatch_EditsThatBlock()
    {
        var path = GetWorkspaceFile("edit/mixed.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "one\ntwo\n---\r\none\r\ntwo\r\n");

        _toolset.EditFile(path, "one\ntwo", "uno\ndos");

        Assert.Equal("uno\ndos\n---\r\none\r\ntwo\r\n", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void EditFile_WhenOnlyTrailingSpacesDiffer_EditsTheUniqueLines(string newLine)
    {
        var path = GetWorkspaceFile("edit/trailing.py");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"def total():{newLine}    x = 1  {newLine}    return x{newLine}");

        _toolset.EditFile(path, "    x = 1\n    return x", "    x = 2\n    return x");

        Assert.Equal($"def total():{newLine}    x = 2{newLine}    return x{newLine}", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditFile_WhenTrailingSpacesDiffer_LeavesDeeperIndentedCopyAlone(bool replaceAll)
    {
        var path = GetWorkspaceFile("edit/nested.py");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "    x = 1  \n        x = 1  \n");

        _toolset.EditFile(path, "    x = 1\n", "    x = 2\n", replaceAll);

        Assert.Equal("    x = 2\n        x = 1  \n", File.ReadAllText(path));
    }

    [Fact]
    public void ApplyPatch_WhenTrailingSpacesMatchTwiceBelowHeader_FailsWithoutEditing()
    {
        var path = GetWorkspaceFile("patch/twice.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "items:\n  - eggs  \n  - eggs \n");
        const string patch = """
            *** Begin Patch
            *** Update File: patch/twice.yaml
            @@ items:
            -  - eggs
            +  - milk
            *** End Patch
            """;

        var exception = Assert.Throws<ToolFailureException>(() => _toolset.ApplyPatch(patch, _workspacePath));

        Assert.Equal("A patch section matched multiple locations.", exception.Message);
        Assert.Equal("items:\n  - eggs  \n  - eggs \n", File.ReadAllText(path));
    }

    [Fact]
    public void ApplyPatch_WhenOnlyTrailingSpacesDiffer_AppliesTheHunk()
    {
        var path = WriteWorkspaceFile("patch/trailing.yaml", "items:  \n  - eggs\n");
        const string patch = """
            *** Begin Patch
            *** Update File: patch/trailing.yaml
            @@
             items:
            -  - eggs
            +  - milk
            *** End Patch
            """;

        _toolset.ApplyPatch(patch, _workspacePath);

        Assert.Equal("items:\n  - milk\n", File.ReadAllText(path).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void ApplyPatch_WhenIndentationDiffers_StillFails()
    {
        var path = WriteWorkspaceFile("patch/indent.yaml", "items:\n    - eggs\n");
        const string patch = """
            *** Begin Patch
            *** Update File: patch/indent.yaml
            @@
            -  - eggs
            +  - milk
            *** End Patch
            """;

        var exception = Assert.Throws<ToolFailureException>(() => _toolset.ApplyPatch(patch, _workspacePath));

        Assert.Equal("A patch section could not be located.", exception.Message);
        Assert.Equal("items:\n    - eggs\n", File.ReadAllText(path).ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ApplyPatch_UpdateFile_KeepsTheFileLineEnding(string newLine)
    {
        var path = GetWorkspaceFile("patch/endings.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"alpha{newLine}beta{newLine}gamma{newLine}");
        const string patch = """
            *** Begin Patch
            *** Update File: patch/endings.txt
            @@
             alpha
            -beta
            +bravo
             gamma
            *** End Patch
            """;

        _toolset.ApplyPatch(patch, _workspacePath);

        Assert.Equal($"alpha{newLine}bravo{newLine}gamma{newLine}", File.ReadAllText(path));
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
    public void WriteAndRead_RouteEveryLogicalRootToItsGrantedFolder()
    {
        string defaultPath = Path.Combine(_workspacePath, "default");
        string researchPath = Path.Combine(_workspacePath, "research");
        string dataPath = Path.Combine(_workspacePath, "private");
        string referencePath = Path.Combine(_workspacePath, "reference");
        Directory.CreateDirectory(defaultPath);
        Directory.CreateDirectory(researchPath);
        Directory.CreateDirectory(dataPath);
        Directory.CreateDirectory(referencePath);
        var toolset = new CodingToolset(() =>
        [
            new(defaultPath, "default", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/workspace/default"),
            new(researchPath, "research", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/workspace/research"),
            new(dataPath, "private", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/private"),
            new(referencePath, "reference", DirectorySourceKind.LocalPath, WorkspaceAccessMode.ReadWrite, AgentPath: "/mnt/reference")
        ]);

        toolset.WriteFile("workspace/default/poem.txt", "default");
        toolset.WriteFile("workspace/research/report.txt", "research");
        toolset.WriteFile("private/drafts/note.txt", "private");
        toolset.WriteFile("mnt/reference/result.txt", "reference");
        toolset.ApplyPatch("""
            *** Begin Patch
            *** Add File: patched.txt
            +patched
            *** End Patch
            """, "workspace/research");

        Assert.Contains("default", toolset.ReadFile("workspace/default/poem.txt"), StringComparison.Ordinal);
        Assert.Equal("default", File.ReadAllText(Path.Combine(defaultPath, "poem.txt")));
        Assert.Equal("research", File.ReadAllText(Path.Combine(researchPath, "report.txt")));
        Assert.Equal("patched", File.ReadAllText(Path.Combine(researchPath, "patched.txt")).Trim());
        Assert.Equal("private", File.ReadAllText(Path.Combine(dataPath, "drafts", "note.txt")));
        Assert.Equal("reference", File.ReadAllText(Path.Combine(referencePath, "result.txt")));
        Assert.False(Directory.Exists(Path.Combine(defaultPath, "workspace")));
        Assert.False(Directory.Exists(Path.Combine(defaultPath, "private")));
        Assert.False(Directory.Exists(Path.Combine(defaultPath, "mnt")));
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
