namespace Oscova.Agents.Tests;

public sealed class ToolNamingTests
{
    [Theory]
    [InlineData("Tavily Search", "search-web", "tavily_search__search-web")]
    [InlineData("Tavily   Search", "search web", "tavily_search__search_web")]
    [InlineData("Tavily @@@ Search", "search-web", "tavily_search__search-web")]
    [InlineData("my-server", "my-tool", "my-server__my-tool")]
    [InlineData("Server", "Tool", "server__tool")]
    public void BuildMcpQualifiedName_UsesServerThenToolOnly(
        string serverName,
        string toolName,
        string expected)
    {
        var name = ToolNaming.BuildMcpQualifiedName(serverName, toolName);

        Assert.Equal(expected, name);
    }
}
