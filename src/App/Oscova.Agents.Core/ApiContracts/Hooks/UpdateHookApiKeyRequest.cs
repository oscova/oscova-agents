namespace Oscova.Agents.Core.ApiContracts.Hooks;

/// <summary>
/// Request to enable or disable an inbound hook API key.
/// </summary>
public sealed class UpdateHookApiKeyRequest
{
    public bool IsEnabled { get; set; }
}