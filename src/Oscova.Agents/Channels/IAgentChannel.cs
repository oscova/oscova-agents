using System.Threading;
using System.Threading.Tasks;
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Channels;

/// <summary>Declares and creates one plugin channel without starting network work during registration.</summary>
public interface IAgentChannel
{
    /// <summary>Gets this channel's runtime identity and presentation.</summary>
    ChannelDefinition Definition { get; }
    /// <summary>Reads plugin-owned configuration and prepares one consistent, validated runtime snapshot.</summary>
    Task<ChannelRuntimeConfiguration> ReadConfigurationAsync(AgentPluginContext context, bool requireComplete, CancellationToken ct = default);
}
