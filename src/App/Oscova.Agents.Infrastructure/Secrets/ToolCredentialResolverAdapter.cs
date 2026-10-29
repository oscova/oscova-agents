using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Infrastructure.Plugins;

namespace Oscova.Agents.Infrastructure.Secrets;

internal sealed class ToolCredentialResolverAdapter(
    ICredentialService credentialService,
    IReadOnlySet<string> allowedCredentialKeys,
    string? pluginId) : IToolCredentialResolver
{
    public Task<string?> ResolveAsync(string credentialKey, string fieldName, CancellationToken ct = default)
    {
        if (!IsAllowed(credentialKey))
        {
            return Task.FromResult<string?>(null);
        }

        return credentialService.ResolveFieldAsync(StorageKey(credentialKey), fieldName, ct);
    }

    public Task<string?> ResolveAsync(string credentialKey, CancellationToken ct = default)
    {
        if (!IsAllowed(credentialKey))
        {
            return Task.FromResult<string?>(null);
        }

        return credentialService.ResolveFieldAsync(StorageKey(credentialKey), ct);
    }

    private bool IsAllowed(string credentialKey)
        => !string.IsNullOrWhiteSpace(credentialKey) &&
            !credentialKey.Trim().StartsWith("oauth:", StringComparison.OrdinalIgnoreCase) &&
            allowedCredentialKeys.Contains(credentialKey.Trim());

    private string StorageKey(string credentialKey)
        => pluginId is null ? credentialKey.Trim() : PluginCredentialKeys.Qualify(pluginId, credentialKey);
}
