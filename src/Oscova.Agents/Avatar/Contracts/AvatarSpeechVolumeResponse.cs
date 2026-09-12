using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for speech volume change requests.
    /// </summary>
    public class AvatarSpeechVolumeResponse : IAvatarBridgeResponse
    {
        /// <summary>
        /// Indicates whether the volume change was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The applied volume level (0.0 to 1.0).
        /// </summary>
        [JsonPropertyName("Volume")]
        public float Volume { get; set; }
    }
}
