using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Secrets.Credentials;

namespace Oscova.Agents.Infrastructure.Secrets;

/// <summary>
/// Provides a no-secret fallback for redaction surfaces that are not attached to a user scope.
/// </summary>
internal sealed class EmptyCredentialService : ICredentialService
{
    public Task<IReadOnlyList<CredentialListItem>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CredentialListItem>>([]);

    public Task<IReadOnlyList<UserCredential>> GetAllRowsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<UserCredential>>([]);

    public Task<UserCredential?> GetByKeyAsync(string credentialKey, CancellationToken ct = default)
        => Task.FromResult<UserCredential?>(null);

    public Task<bool> HasValueAsync(string credentialKey, string fieldName, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<string?> ResolveFieldAsync(string credentialKey, string fieldName, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<string?> ResolveFieldAsync(string credentialKey, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task SaveAsync(CredentialWrite credential, CancellationToken ct = default)
        => throw new NotSupportedException("The fallback credential service is read-only.");

    public Task DeleteAsync(string credentialKey, CancellationToken ct = default)
        => throw new NotSupportedException("The fallback credential service is read-only.");

    public Task<int> DeleteAllForUserAsync(string userId, CancellationToken ct = default)
        => Task.FromResult(0);
}
