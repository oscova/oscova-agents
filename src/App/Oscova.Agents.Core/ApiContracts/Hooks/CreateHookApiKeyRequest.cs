namespace Oscova.Agents.Core.ApiContracts.Hooks;

/// <summary>
/// Request to create an inbound hook API key.
/// </summary>
public sealed class CreateHookApiKeyRequest
{
    public string Name { get; set; } = string.Empty;
    public IReadOnlyList<string> Scopes { get; set; } = [];
    public IReadOnlyList<string> AllowedTaskSlugs { get; set; } = [];
}

/// <summary>
/// Response containing created key metadata and the one-time plaintext token.
/// </summary>
public sealed class CreatedHookApiKey
{
    public HookApiKeySummary Key { get; set; } = new();
    public string PlaintextToken { get; set; } = string.Empty;
}

/// <summary>
/// API-safe hook API-key metadata that excludes hashes and plaintext tokens.
/// </summary>
public sealed class HookApiKeySummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string TokenPrefix { get; set; } = string.Empty;
    public IReadOnlyList<string> Scopes { get; set; } = [];
    public IReadOnlyList<string> AllowedTaskSlugs { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
