using Oscova.Agents.Channels;
namespace Oscova.Agents.Plugins.Docker;
/// <summary>Contributes the optional Docker runtime, shared image panel and container process launcher.</summary>
public sealed class DockerAgentPlugin : IAgentPlugin
{
    /// <summary>Registers factories sharing one device runtime without starting Docker.</summary>
    public void Configure(AgentPluginBuilder builder)
    {
        DockerExecutionProvider? runtime = null;
        DockerExecutionProvider Resolve(AgentPluginContext context) => runtime ??= new(context);
        builder.UseResources<PluginText>()
            .AddExecutionProvider(new(DockerExecutionProvider.Definition, context => Resolve(context)))
            .AddContainerLauncher(new(context => Resolve(context)))
            .AddSettingsPanel(new("image", PluginSettingsSlots.ExecutionEnvironment, DockerExecutionProvider.Id,
                typeof(DockerImagePanelComponent), context => new DockerPanelContext(
                    new DockerImagePanel(Resolve(context.Plugin).Images, context), context.Ui,
                    $"plugin:{context.PluginId}:dockerfile"), PluginSettingsAccess.Operator,
                Help: new PluginHelpTopic("image", "Oscova.Agents.Plugins.Docker.Help.image.md"))
                { RelatedHelp = [new PluginHelpTopic("dockerfile", "Oscova.Agents.Plugins.Docker.Help.dockerfile.md")] });
    }
}
