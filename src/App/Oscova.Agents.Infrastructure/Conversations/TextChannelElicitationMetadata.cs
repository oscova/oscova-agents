using Oscova.Agents.Core.Conversation;

namespace Oscova.Agents.Infrastructure.Conversations;

/// <summary>Identifies plugin-channel question metadata at the application router boundary.</summary>
public sealed class TextChannelElicitationMetadata
{
    /// <summary>Supplies the original connection callback and complete reply correlation key.</summary>
    public required PluginElicitationOrigin ChannelOriginator { get; init; }
}
