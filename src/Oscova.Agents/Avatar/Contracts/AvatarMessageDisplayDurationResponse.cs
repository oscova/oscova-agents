using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for avatar message display duration changes.
    /// </summary>
    public class AvatarMessageDisplayDurationResponse : IAvatarBridgeResponse
    {
        /// <summary>
        /// Indicates whether the display duration change was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The applied display duration in seconds.
        /// </summary>
        [JsonPropertyName("DurationSeconds")]
        public float DurationSeconds { get; set; }
    }
}
