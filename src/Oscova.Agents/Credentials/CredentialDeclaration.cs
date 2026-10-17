using System.Collections.Generic;

namespace Oscova.Agents.Credentials;

/// <summary>A code-declared credential slot: a stable key, a label, and the field set the user fills.</summary>
public sealed class CredentialDeclaration
{
    /// <summary>Stable credential key the slot is stored under.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>User-facing slot name.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Optional plugin resource key for the slot name.</summary>
    public string? LabelKey { get; init; }

    /// <summary>One-line description shown under the slot.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Optional plugin resource key for the description.</summary>
    public string? DescriptionKey { get; init; }

    /// <summary>Whether the slot is filled by a user or by an administrator for the whole system.</summary>
    public CredentialScope Scope { get; init; } = CredentialScope.User;

    /// <summary>The fields the user supplies when filling this slot.</summary>
    public IReadOnlyList<CredentialFieldDefinition> Fields { get; init; } = [];
}
