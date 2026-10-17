using Oscova.Agents.Channels;

namespace Oscova.Agents.Plugins.Mxc;

/// <summary>Contributes native execution and its compiled settings card through the shared plugin contracts.</summary>
public sealed class MxcAgentPlugin : IAgentPlugin
{
    /// <summary>Registers the runtime without probing the executing machine during discovery.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        builder.UseResources<PluginText>()
            .AddExecutionProvider(new(MxcExecutionProvider.Definition, context => new MxcExecutionProvider(context)))
            .AddSettingsPanel(new("execution", PluginSettingsSlots.ExecutionEnvironment, MxcExecutionProvider.Id,
                typeof(MxcSettingsComponent), context => context, PluginSettingsAccess.Operator,
                HeadingKey: PluginTextKeys.Title,
                Help: new PluginHelpTopic("execution", "Oscova.Agents.Plugins.Mxc.Help.execution.md")));
    }
}
