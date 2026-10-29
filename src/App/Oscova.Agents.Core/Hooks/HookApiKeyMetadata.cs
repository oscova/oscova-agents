namespace Oscova.Agents.Core.Hooks;

/// <summary>
/// Safe hook API key metadata that excludes the token hash and plaintext token.
/// </summary>
public sealed class HookApiKeyMetadata
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
