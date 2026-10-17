using Oscova.Agents.Channels;

namespace Oscova.Agents.Plugins;

/// <summary>Registers one channel as its definition, validator, and connection factory.</summary>
public sealed record AgentChannelContribution(IAgentChannel Channel)
{
    /// <summary>Gets the channel definition for catalog projection.</summary>
    public ChannelDefinition Definition => Channel.Definition;
}
