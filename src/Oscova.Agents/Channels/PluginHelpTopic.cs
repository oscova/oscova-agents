using System.Collections.Generic;

namespace Oscova.Agents.Channels;

/// <summary>Declares neutral and optional translated embedded help resources owned by a plugin.</summary>
public sealed record PluginHelpTopic(string Id, string ResourceName)
{
    /// <summary>Maps culture names to translated embedded Markdown resources.</summary>
    public IReadOnlyDictionary<string, string> Translations { get; init; }
        = new Dictionary<string, string>();
}
