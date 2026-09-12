using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for graphics preset requests.
    /// </summary>
    public class AvatarGraphicsPresetResponse : IAvatarBridgeResponse
    {
        /// <summary>
        /// Indicates whether the preset change was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The applied graphics preset level.
        /// </summary>
        [JsonPropertyName("Level")]
        public int Level { get; set; }

        /// <summary>
        /// The resolved preset name (Low/Medium/High/Epic/Cinematic/Unknown).
        /// </summary>
        [JsonPropertyName("PresetName")]
        public string PresetName { get; set; } = string.Empty;

        /// <summary>
        /// The effective runtime ray-tracing state.
        /// </summary>
        [JsonPropertyName("RayTracingEnabled")]
        public bool RayTracingEnabled { get; set; }
    }
}
