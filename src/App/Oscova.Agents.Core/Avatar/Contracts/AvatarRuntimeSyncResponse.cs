namespace Oscova.Agents.Core.Avatar.Contracts;

/// <summary>
/// Reports whether saved avatar settings were applied to the live avatar engine.
/// </summary>
public sealed class AvatarRuntimeSyncResponse
{
    public bool Success { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    public int? RetryAfterSeconds { get; set; }

    public bool Connected { get; set; }

    public bool SettingsApplied { get; set; }
}
