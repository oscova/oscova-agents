namespace Oscova.Agents.Host.Services.UI;

/// <summary>
/// Keeps chat sidebar disclosure state alive for the current Blazor circuit.
/// </summary>
public sealed class ChatSidebarState
{
    private readonly Dictionary<string, bool> _expandedCategories = new(StringComparer.Ordinal);

    public bool BrowserStateLoaded { get; private set; }

    public void EnsureCategory(string categoryKey)
    {
        _expandedCategories.TryAdd(categoryKey, false);
    }

    public bool IsExpanded(string categoryKey) =>
        _expandedCategories.TryGetValue(categoryKey, out var expanded) && expanded;

    public void SetExpanded(string categoryKey, bool expanded)
    {
        _expandedCategories[categoryKey] = expanded;
    }

    public void ApplyBrowserState(IReadOnlyDictionary<string, bool>? expandedCategories)
    {
        BrowserStateLoaded = true;

        if (expandedCategories is null)
        {
            return;
        }

        foreach (var (categoryKey, expanded) in expandedCategories)
        {
            _expandedCategories[categoryKey] = expanded;
        }
    }

    public IReadOnlyDictionary<string, bool> Snapshot() =>
        new Dictionary<string, bool>(_expandedCategories, StringComparer.Ordinal);
}
