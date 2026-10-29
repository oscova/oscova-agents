using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Core.ApiContracts.Hooks;

/// <summary>
/// Admin summary of outbound hooks and inbound hook API keys.
/// </summary>
public sealed class HookSettingsSummary
{
    public IReadOnlyList<HookDefinition> Hooks { get; set; } = [];
    public IReadOnlyList<HookApiKeySummary> ApiKeys { get; set; } = [];
}

/// <summary>
/// Request to replace outbound hook settings.
/// </summary>
public sealed class UpdateHookSettingsRequest
{
    public IReadOnlyList<HookDefinition> Hooks { get; set; } = [];
}
