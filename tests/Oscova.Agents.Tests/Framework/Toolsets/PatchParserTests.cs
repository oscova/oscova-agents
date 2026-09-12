using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Tests.Framework.Toolsets;

public class PatchParserTests
{
    [Fact]
    public void Parse_ReturnsExpectedOperations()
    {
        const string patch = """
            *** Begin Patch
            *** Add File: src/NewFile.cs
            +namespace Example;
            +public class NewFile { }
            *** Update File: src/Existing.cs
            *** Move to: src/Renamed.cs
            @@ class Existing
            -public class Existing { }
            +public class Existing { public int Count { get; set; } }
            *** Delete File: src/Obsolete.cs
            *** End Patch
            """;

        var document = PatchParser.Parse(patch);

        Assert.Equal(3, document.Operations.Count);

        var add = Assert.IsType<AddFileOperation>(document.Operations[0]);
        Assert.Equal("src/NewFile.cs", add.Path);
        Assert.Contains("namespace Example;", add.Content);

        var update = Assert.IsType<UpdateFileOperation>(document.Operations[1]);
        Assert.Equal("src/Existing.cs", update.Path);
        Assert.Equal("src/Renamed.cs", update.MoveTo);
        Assert.Single(update.Hunks);

        var delete = Assert.IsType<DeleteFileOperation>(document.Operations[2]);
        Assert.Equal("src/Obsolete.cs", delete.Path);
    }

    [Fact]
    public void Parse_StripsHeredocWrapperInLenientMode()
    {
        const string patch = """
            <<'PATCH'
            *** Begin Patch
            *** Add File: notes.txt
            +hello
            *** End Patch
            PATCH
            """;

        var document = PatchParser.Parse(patch);

        var add = Assert.IsType<AddFileOperation>(Assert.Single(document.Operations));
        Assert.Equal("notes.txt", add.Path);
        Assert.Equal("hello", add.Content);
    }
}
