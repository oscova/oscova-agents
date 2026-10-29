using Oscova.Agents.Core.Secrets.Credentials;

namespace Oscova.Agents.Core.Secrets;

/// <summary>Reads system credentials and saves stored fields under an explicit administrator key owner.</summary>
public interface ISystemCredentialService
{
    Task<IReadOnlyList<CredentialListItem>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UserCredential>> GetAllRowsAsync(CancellationToken ct = default);
    Task<UserCredential?> GetByKeyAsync(string credentialKey, CancellationToken ct = default);
    Task<bool> HasValueAsync(string credentialKey, string fieldName, CancellationToken ct = default);
    Task<string?> ResolveFieldAsync(string credentialKey, string fieldName, CancellationToken ct = default);
    Task<string?> ResolveFieldAsync(string credentialKey, CancellationToken ct = default);
    Task SaveAsync(string protectionUserId, CredentialWrite credential, CancellationToken ct = default);
    Task DeleteAsync(string credentialKey, CancellationToken ct = default);
    Task<bool> TryClaimOAuthProviderAsync(string providerId, string pluginId, string credentialKey, CancellationToken ct = default);
    Task DeletePluginNamespaceAsync(string pluginId, CancellationToken ct = default);
}
