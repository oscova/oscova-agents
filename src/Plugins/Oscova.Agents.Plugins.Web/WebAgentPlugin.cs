using Microsoft.Extensions.Logging;
using Oscova.Agents.Plugins.Web.Toolsets;

namespace Oscova.Agents.Plugins.Web;

/// <summary>
/// Registers first-party web and news toolsets for the agent runtime.
/// </summary>
public sealed class WebAgentPlugin : IAgentPlugin
{
    /// <summary>
    /// Adds web search and news toolsets to the plugin builder.
    /// </summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder.AddToolset(context => new WebSearchToolset(
            context.CredentialResolver,
            context.CreateHttpClient(),
            context.LoggerFactory.CreateLogger<WebSearchToolset>()))
            .AddToolset(context => new NewsToolset(
            context.CredentialResolver,
            context.CreateHttpClient(),
            context.LoggerFactory.CreateLogger<NewsToolset>(),
            () => context.UserTimeZone));
    }
}
