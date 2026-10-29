namespace Oscova.Agents.Core.Hooks;

/// <summary>
/// Describes a hook API key to create.
/// </summary>
public sealed class HookApiKeyCreateOptions
{
    public string Name { get; set; } = string.Empty;
    public IReadOnlyList<string> Scopes { get; set; } = [];
    public IReadOnlyList<string> AllowedTaskSlugs { get; set; } = [];
}
