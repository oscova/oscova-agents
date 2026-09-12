using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for resolution change requests.
    /// </summary>
    public class AvatarResolutionResponse : IAvatarBridgeResponse
    {
        /// <summary>
        /// Indicates whether the resolution change was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The applied window width in pixels.
        /// </summary>
        [JsonPropertyName("Width")]
        public int Width { get; set; }

        /// <summary>
        /// The applied window height in pixels.
        /// </summary>
        [JsonPropertyName("Height")]
        public int Height { get; set; }

        /// <summary>
        /// Whether fullscreen mode is enabled.
        /// </summary>
        [JsonPropertyName("Fullscreen")]
        public bool Fullscreen { get; set; }
    }
}
