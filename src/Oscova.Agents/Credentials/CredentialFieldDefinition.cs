namespace Oscova.Agents.Credentials;

/// <summary>Declares one field of a credential type: its name, label, and how it behaves in the editor.</summary>
public sealed class CredentialFieldDefinition
{
    /// <summary>Stable field name used as the key inside stored credential data.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>User-facing label rendered next to the input.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Optional plugin resource key for the field label.</summary>
    public string? LabelKey { get; init; }

    /// <summary>When true the stored value is encrypted at rest and masked in the editor.</summary>
    public bool IsSecret { get; init; } = true;

    /// <summary>When true the field must have a stored value or an environment-variable name.</summary>
    public bool IsRequired { get; init; } = true;

    /// <summary>Optional environment-variable name pre-filled in the editor as a suggestion only.</summary>
    public string? DefaultEnvVariable { get; init; }
}
