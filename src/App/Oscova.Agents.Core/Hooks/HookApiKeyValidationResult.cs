namespace Oscova.Agents.Core.Hooks;

/// <summary>
/// Contains authenticated hook API-key claims that are safe to expose to the host auth layer.
/// </summary>
public sealed class HookApiKeyValidationResult
{
    public bool IsValid { get; init; }
    public string? KeyId { get; init; }
    public string? TokenPrefix { get; init; }
    public IReadOnlySet<string> Scopes { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> AllowedTaskSlugs { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}