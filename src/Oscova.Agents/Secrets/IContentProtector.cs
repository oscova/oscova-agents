namespace Oscova.Agents.Secrets;

/// <summary>
/// Protects durable content for one user under a stable purpose.
/// </summary>
public interface IContentProtector
{
    /// <summary>Returns whether protected content can currently be opened for the user.</summary>
    bool IsAvailable(string userId);

    /// <summary>Protects plaintext for one user and purpose.</summary>
    string Protect(string userId, string purpose, string plaintext);

    /// <summary>Returns plaintext, or null when the key or payload is unavailable.</summary>
    string? Unprotect(string userId, string purpose, string ciphertext);
}
