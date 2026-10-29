using Oscova.Agents.Core.Secrets.Credentials;

namespace Oscova.Agents.Core.ApiContracts.Credentials;

/// <summary>One credential field with its declared metadata and current saved state. Never carries a secret value.</summary>
public sealed class CredentialFieldContract
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
    public bool IsRequired { get; set; }

    /// <summary>Environment-variable name suggested in the editor when the field has no saved state.</summary>
    public string? DefaultEnvVariable { get; set; }

    public CredentialFieldSource Source { get; set; } = CredentialFieldSource.Stored;

    /// <summary>True when the field has a usable stored value or environment-variable name.</summary>
    public bool IsSet { get; set; }

    /// <summary>The saved environment-variable name when the field is env-sourced.</summary>
    public string? EnvVariable { get; set; }
}
