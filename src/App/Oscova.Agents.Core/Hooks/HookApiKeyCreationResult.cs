namespace Oscova.Agents.Core.Hooks;

/// <summary>
/// Contains created hook API key metadata and the one-time plaintext token.
/// </summary>
public sealed class HookApiKeyCreationResult
{
    public HookApiKeyMetadata Key { get; set; } = new();
    public string PlaintextToken { get; set; } = string.Empty;
}