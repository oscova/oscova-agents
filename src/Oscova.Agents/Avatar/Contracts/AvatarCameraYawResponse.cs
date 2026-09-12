using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for camera yaw adjustment requests.
    /// </summary>
    public class AvatarCameraYawResponse : IAvatarBridgeResponse
    {
        /// <summary>
        /// Indicates whether the camera yaw change was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The applied yaw value (-5.0 to 5.0).
        /// </summary>
        [JsonPropertyName("Yaw")]
        public float Yaw { get; set; }
    }
}
