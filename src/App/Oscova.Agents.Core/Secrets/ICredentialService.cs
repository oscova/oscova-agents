using Oscova.Agents.Core.Secrets.Credentials;

namespace Oscova.Agents.Core.Secrets;

public interface ICredentialService
{
    Task<IReadOnlyList<CredentialListItem>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Loads every full credential row in one query — for callers that need the field data of every row.</summary>
    Task<IReadOnlyList<UserCredential>> GetAllRowsAsync(CancellationToken ct = default);

    Task<UserCredential?> GetByKeyAsync(string credentialKey, CancellationToken ct = default);

    /// <summary>True when the named field has a usable value (stored non-empty, or an env var name set).</summary>
    Task<bool> HasValueAsync(string credentialKey, string fieldName, CancellationToken ct = default);

    /// <summary>Resolves one field to its effective value (decrypts, or reads the env var).</summary>
    Task<string?> ResolveFieldAsync(string credentialKey, string fieldName, CancellationToken ct = default);

    /// <summary>
    /// Resolves the sole field of a single-field credential — returns null when the credential
    /// has zero or more than one field. Used by API-key tools and model-provider credentials,
    /// whose callers do not know the field name.
    /// </summary>
    Task<string?> ResolveFieldAsync(string credentialKey, CancellationToken ct = default);

    /// <summary>Inserts or updates a credential and its field set.</summary>
    Task SaveAsync(CredentialWrite credential, CancellationToken ct = default);

    Task DeleteAsync(string credentialKey, CancellationToken ct = default);

    /// <summary>
    /// Deletes every credential row owned by the supplied user.
    /// </summary>
    Task<int> DeleteAllForUserAsync(string userId, CancellationToken ct = default);
}
