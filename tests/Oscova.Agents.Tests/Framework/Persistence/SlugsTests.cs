using Oscova.Agents.Persistence;

namespace Oscova.Agents.Tests.Framework.Persistence;

public sealed class SlugsTests
{
    [Theory]
    [InlineData("My Agent", "my-agent")]
    [InlineData("Mixed CASE 123", "mixed-case-123")]
    [InlineData("  Hello   World  ", "hello-world")]
    [InlineData("Foo!!!Bar???Baz", "foo-bar-baz")]
    [InlineData("---weird---", "weird")]
    [InlineData("Über Café", "ber-caf")]
    public void ForAgentFolder_NormalizesInput(string input, string expected)
    {
        Assert.Equal(expected, Slugs.ForAgentFolder(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("---")]
    public void ForAgentFolder_FallsBackToAgent_WhenInputHasNoUsableCharacters(string input)
    {
        Assert.Equal("agent", Slugs.ForAgentFolder(input));
    }

    [Fact]
    public void ForAgentFolder_CapsLengthAndTruncatesAtWordBoundary()
    {
        var name = string.Join(" ", Enumerable.Repeat("word", 20));

        var slug = Slugs.ForAgentFolder(name);

        Assert.True(slug.Length <= 60, $"slug length was {slug.Length}");
        Assert.False(slug.EndsWith('-'));
        Assert.DoesNotContain(' ', slug);
        Assert.StartsWith("word-word", slug);
    }

    [Theory]
    [InlineData("MyKey", "mykey")]
    [InlineData("  GitHub Token  ", "github-token")]
    [InlineData("API!!Key??v2", "api-key-v2")]
    [InlineData("---hello---", "hello")]
    public void ForCredentialAlias_NormalizesInput(string input, string expected)
    {
        Assert.Equal(expected, Slugs.ForCredentialAlias(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("---")]
    public void ForCredentialAlias_ReturnsEmpty_WhenInputHasNoUsableCharacters(string input)
    {
        Assert.Equal(string.Empty, Slugs.ForCredentialAlias(input));
    }

    [Fact]
    public void ForCredentialAlias_DoesNotCapLength()
    {
        var alias = new string('a', 200);

        var slug = Slugs.ForCredentialAlias(alias);

        Assert.Equal(200, slug.Length);
    }

    [Theory]
    [InlineData("Daily Report", "daily-report")]
    [InlineData("Mixed CASE 123", "mixed-case-123")]
    [InlineData("Hook!!!Task???Run", "hook-task-run")]
    [InlineData("Über Café", "ber-caf")]
    public void ForTaskTrigger_NormalizesInput(string input, string expected)
    {
        Assert.Equal(expected, Slugs.ForTaskTrigger(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void ForTaskTrigger_FallsBackToTask_WhenInputHasNoUsableCharacters(string input)
    {
        Assert.Equal("task", Slugs.ForTaskTrigger(input));
    }

    [Fact]
    public void ForTaskTrigger_CapsLength()
    {
        var slug = Slugs.ForTaskTrigger(new string('a', 120));

        Assert.Equal(80, slug.Length);
    }

    [Fact]
    public void MakeUnique_ReturnsOriginal_WhenSlugIsFree()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "foo", "bar" };

        Assert.Equal("baz", Slugs.MakeUnique("baz", existing));
    }

    [Fact]
    public void MakeUnique_AppendsNumericSuffix_WhenSlugIsTaken()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agent" };

        Assert.Equal("agent-2", Slugs.MakeUnique("agent", existing));
    }

    [Fact]
    public void MakeUnique_IncrementsSuffix_UntilFreeSlotFound()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "agent", "agent-2", "agent-3", "agent-4"
        };

        Assert.Equal("agent-5", Slugs.MakeUnique("agent", existing));
    }
}
