namespace Oscova.Agents.Core.ApiContracts.Plugins;

/// <summary>
/// Saves the installed plugin identifiers explicitly permitted to load at startup.
/// </summary>
public sealed class UpdatePluginSettingsRequest
{
    public IReadOnlyList<string> EnabledPluginIds { get; set; } = [];
}
