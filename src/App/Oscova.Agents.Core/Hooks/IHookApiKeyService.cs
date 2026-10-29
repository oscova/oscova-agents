namespace Oscova.Agents.Core.Hooks;

/// <summary>
/// Creates and validates bearer tokens used by inbound hook APIs.
/// </summary>
public interface IHookApiKeyService
{
    Task<HookApiKeyCreationResult> CreateAsync(HookApiKeyCreateOptions options, CancellationToken ct = default);
    Task<IReadOnlyList<HookApiKeyMetadata>> ListAsync(CancellationToken ct = default);
    Task<bool> SetEnabledAsync(string id, bool isEnabled, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
    Task<HookApiKeyValidationResult> ValidateAsync(string token, CancellationToken ct = default);
}