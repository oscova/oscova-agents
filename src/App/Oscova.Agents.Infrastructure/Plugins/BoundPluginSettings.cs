using Oscova.Agents.Plugins;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Infrastructure.Plugins;

/// <summary>Restricts settings access to the owning plugin and the scope admitted by the Host.</summary>
public sealed class BoundPluginSettings(IAppSettingsRepository repository, string pluginId, string scopeId,
    Func<bool, CancellationToken, Task> requireAccess) : IPluginSettings
{
    /// <inheritdoc />
    public async Task<string?> ReadAsync(string key, CancellationToken ct = default)
    {
        await requireAccess(false, ct).ConfigureAwait(false);
        return await repository.GetSettingsAsync(scopeId, PluginSettingsNamespace.Resolve(pluginId, key), ct).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async Task WriteAsync(string key, string content, CancellationToken ct = default)
    {
        await requireAccess(true, ct).ConfigureAwait(false);
        await repository.SaveSettingsAsync(scopeId, PluginSettingsNamespace.Resolve(pluginId, key), content, ct).ConfigureAwait(false);
    }
}
