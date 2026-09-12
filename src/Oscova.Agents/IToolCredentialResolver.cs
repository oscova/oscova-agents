using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents;

/// <summary>
/// Resolves stored credential values for tools without exposing the host credential store.
/// </summary>
public interface IToolCredentialResolver
{
    /// <summary>
    /// Resolves a named field of a credential requested by a tool.
    /// </summary>
    /// <param name="credentialKey">The stable key identifying the credential.</param>
    /// <param name="fieldName">The credential field to read.</param>
    /// <param name="ct">A token that can cancel the lookup.</param>
    /// <returns>The stored field value, or <see langword="null"/> when the credential or field is unavailable.</returns>
    Task<string?> ResolveAsync(string credentialKey, string fieldName, CancellationToken ct = default);

    /// <summary>
    /// Resolves the value of a single-field credential.
    /// </summary>
    /// <param name="credentialKey">The stable key identifying the credential.</param>
    /// <param name="ct">A token that can cancel the lookup.</param>
    /// <returns>The stored credential value, or <see langword="null"/> when it is unavailable.</returns>
    Task<string?> ResolveAsync(string credentialKey, CancellationToken ct = default);
}
