using Oscova.Agents.Core.Secrets.Credentials;

namespace Oscova.Agents.Core.ApiContracts.Credentials;

/// <summary>The editor payload to upsert a credential.</summary>
public sealed class SaveCredentialRequest
{
    /// <summary>A declared slot key, an existing free-form key, or empty for a new free-form credential.</summary>
    public string CredentialKey { get; set; } = string.Empty;
    public bool ConfirmInterruptions { get; set; }
    public string Alias { get; set; } = string.Empty;
    public string? Description { get; set; }
    public IReadOnlyList<SaveCredentialFieldContract> Fields { get; set; } = [];
}

/// <summary>One field as submitted by the editor.</summary>
public sealed class SaveCredentialFieldContract
{
    public string Name { get; set; } = string.Empty;

    public CredentialFieldSource Source { get; set; } = CredentialFieldSource.Stored;

    /// <summary>Plain text the user typed; null or blank means "keep current" for a stored secret field.</summary>
    public string? Value { get; set; }

    /// <summary>Environment-variable name when <see cref="Source"/> is Env.</summary>
    public string? EnvVariable { get; set; }
}
