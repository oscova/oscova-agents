using Oscova.Agents.Plugins.Web;
using Oscova.Agents.Plugins.Web.Toolsets;

namespace Oscova.Agents.Plugins.Tests.Web;

public sealed class WebAgentPluginTests
{
    [Fact]
    public void Configure_RegistersWebAndNewsToolsets()
    {
        var builder = new AgentPluginBuilder();

        new WebAgentPlugin().Configure(builder);

        Assert.Contains(builder.Toolsets, contribution => contribution.ToolsetType == typeof(WebSearchToolset));
        Assert.Contains(builder.Toolsets, contribution => contribution.ToolsetType == typeof(NewsToolset));
    }
}
