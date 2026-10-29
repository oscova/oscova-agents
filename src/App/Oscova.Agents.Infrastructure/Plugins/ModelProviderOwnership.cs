using Oscova.Agents.Plugins;

namespace Oscova.Agents.Infrastructure.Plugins;

/// <summary>Resolves model settings and diagnostic code from successfully admitted plugin contributions.</summary>
public sealed class ModelProviderOwnership
{
    private readonly Dictionary<string, (LoadedAgentPlugin Plugin, int ContributionIndex)> _owners = new(StringComparer.Ordinal);

    /// <summary>Records the installed plugin after its provider has passed admission.</summary>
    public void Add(string type, LoadedAgentPlugin plugin, int contributionIndex) => _owners.Add(type, (plugin, contributionIndex));

    /// <summary>Returns the admitted owner without inspecting or loading a native runtime.</summary>
    public LoadedAgentPlugin? Find(string type) => _owners.GetValueOrDefault(type).Plugin;

    /// <summary>Identifies the admitted factory so diagnostics activate no other provider contributions.</summary>
    public int ContributionIndex(string type) => _owners[type].ContributionIndex;
}
