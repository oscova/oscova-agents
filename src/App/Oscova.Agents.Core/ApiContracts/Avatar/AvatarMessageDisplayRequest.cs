using System.Text.Json.Serialization;

namespace Oscova.Agents.Core.ApiContracts.Avatar;

/// <summary>Displays a message temporarily through the Avatar bridge.</summary>
public sealed class AvatarMessageDisplayRequest
{
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("durationSeconds")]
    public float DurationSeconds { get; init; }
}
