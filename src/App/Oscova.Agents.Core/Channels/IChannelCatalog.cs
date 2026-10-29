using Oscova.Agents.Channels;

namespace Oscova.Agents.Core.Channels;

/// <summary>Exposes validated active channel definitions to private product consumers.</summary>
public interface IChannelCatalog
{
    /// <summary>Gets the active channel definitions.</summary>
    IReadOnlyList<ChannelDefinition> Channels { get; }
}
