using System.Collections.Generic;

namespace Oscova.Agents.Channels;

/// <summary>Declares a channel's runtime identity and presentation independently of its settings UI.</summary>
public sealed class ChannelDefinition
{
    /// <summary>Gets the stable channel identifier.</summary>
    public required string Id { get; init; }
    /// <summary>Gets the fallback display title.</summary>
    public required string Title { get; init; }
    /// <summary>Gets the fallback description.</summary>
    public required string Description { get; init; }
    /// <summary>Gets the exact manifest name of this plugin's embedded SVG icon.</summary>
    public string? IconResourceName { get; init; }
    /// <summary>Gets an optional plugin localization key for the title.</summary>
    public string? TitleKey { get; init; }
    /// <summary>Gets an optional plugin localization key for the description.</summary>
    public string? DescriptionKey { get; init; }
    /// <summary>Gets embedded help supplied by this channel's plugin.</summary>
    public PluginHelpTopic? Help { get; init; }
    /// <summary>Gets other declared help pages linked from this channel's primary help.</summary>
    public IReadOnlyList<PluginHelpTopic> RelatedHelp { get; init; } = [];
    /// <summary>Gets credential keys required to start this channel.</summary>
    public IReadOnlyList<string> CredentialKeys { get; init; } = [];
    /// <summary>Gets whether the channel supports outbound notifications.</summary>
    public bool SupportsNotifications { get; init; }
}
