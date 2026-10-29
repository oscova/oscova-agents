namespace Oscova.Agents.Core.Secrets.Credentials;

/// <summary>A credential to upsert: metadata plus the field set the caller wants stored.</summary>
public sealed class CredentialWrite
{
    public string CredentialKey { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsManaged { get; set; }
    public IReadOnlyList<CredentialFieldWrite> Fields { get; set; } = [];
}

/// <summary>One field as supplied by a caller (raw, not yet encrypted).</summary>
public sealed class CredentialFieldWrite
{
    public string Name { get; set; } = string.Empty;
    public bool IsSecret { get; set; }

    public CredentialFieldSource Source { get; set; } = CredentialFieldSource.Stored;

    /// <summary>Plain text value when <see cref="Source"/> is Stored; ignored when Env.</summary>
    public string? Value { get; set; }

    /// <summary>Environment variable name when <see cref="Source"/> is Env.</summary>
    public string? EnvVariable { get; set; }
}
