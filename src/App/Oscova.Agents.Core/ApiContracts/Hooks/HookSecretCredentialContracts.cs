namespace Oscova.Agents.Core.ApiContracts.Hooks;

/// <summary>
/// API-safe metadata for a system credential created for hook secret headers.
/// </summary>
public sealed class HookSecretCredentialSummary
{
    public string CredentialKey { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSet { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Request to create or update a single-field hook secret credential.
/// </summary>
public sealed class SaveHookSecretCredentialRequest
{
    public string? CredentialKey { get; set; }
    public string Alias { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? SecretValue { get; set; }
}