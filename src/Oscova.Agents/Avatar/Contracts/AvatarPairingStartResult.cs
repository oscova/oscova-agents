using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts;

/// <summary>Reports whether an engine displayed a pairing code and how long it remains valid.</summary>
public sealed class AvatarPairingStartResult
{
    /// <summary>Gets whether pairing was started.</summary>
    [JsonPropertyName("Success")]
    public bool Success { get; init; }

    /// <summary>Gets the stable engine or local error code.</summary>
    [JsonPropertyName("ErrorCode")]
    public string? ErrorCode { get; init; }

    /// <summary>Gets a human-readable description.</summary>
    [JsonPropertyName("Description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets the remaining pairing-code lifetime.</summary>
    [JsonPropertyName("ExpiresInSeconds")]
    public int? ExpiresInSeconds { get; init; }

    /// <summary>Gets the suggested delay before a manual retry, when supplied.</summary>
    [JsonPropertyName("RetryAfterSeconds")]
    public int? RetryAfterSeconds { get; init; }
}
