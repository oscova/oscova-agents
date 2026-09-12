namespace Oscova.Agents.Plugins;

/// <summary>
/// Entry point implemented by trusted in-process agent plugins.
/// </summary>
public interface IAgentPlugin
{
    /// <summary>
    /// Registers toolsets and other supported contributions for this plugin.
    /// </summary>
    void Configure(AgentPluginBuilder builder);
}