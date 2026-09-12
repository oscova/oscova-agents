using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts;

/// <summary>Reports a bridge connection, activation, or pairing outcome without exposing credentials.</summary>
public sealed class AvatarBridgeOperationResult
{
    /// <summary>Gets whether the operation succeeded.</summary>
    [JsonPropertyName("Success")]
    public bool Success { get; init; }

    /// <summary>Gets the stable engine or local error code.</summary>
    [JsonPropertyName("ErrorCode")]
    public string? ErrorCode { get; init; }

    /// <summary>Gets a human-readable description.</summary>
    [JsonPropertyName("Description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets the suggested delay before a manual retry, when supplied.</summary>
    [JsonPropertyName("RetryAfterSeconds")]
    public int? RetryAfterSeconds { get; init; }
}
