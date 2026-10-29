namespace Oscova.Agents.Core.Secrets.Credentials;

/// <summary>One field of a credential as persisted in <see cref="UserCredential.Data"/>.</summary>
public sealed class CredentialFieldEntry
{
    public CredentialFieldSource Source { get; set; } = CredentialFieldSource.Stored;

    public bool Secret { get; set; }

    /// <summary>Ciphertext when Secret &amp;&amp; Stored; plain text when !Secret &amp;&amp; Stored; null when Env-sourced.</summary>
    public string? Value { get; set; }

    /// <summary>Environment variable name when <see cref="Source"/> is <see cref="CredentialFieldSource.Env"/>.</summary>
    public string? Env { get; set; }
}
