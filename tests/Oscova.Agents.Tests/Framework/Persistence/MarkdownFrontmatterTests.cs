using Oscova.Agents.Persistence;

namespace Oscova.Agents.Tests.Framework.Persistence;

public sealed class MarkdownFrontmatterTests
{
    [Theory]
    [InlineData("---\n---\nbody")]
    [InlineData("---\r\n---\r\nbody")]
    public void Split_ParsesCompactEmptyFrontmatter(string fileText)
    {
        var (yaml, body) = MarkdownFrontmatter.Split(fileText);

        Assert.Equal(string.Empty, yaml);
        Assert.Equal("body", body);
    }
}