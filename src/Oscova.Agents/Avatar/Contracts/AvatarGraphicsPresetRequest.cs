using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for setting the global graphics quality preset.
    /// </summary>
    public class AvatarGraphicsPresetRequest
    {
        /// <summary>
        /// The graphics preset level. Values 0 through 4 map to Low, Medium, High, Epic, and Cinematic.
        /// </summary>
        [JsonPropertyName("Level")]
        public int Level { get; set; }

        /// <summary>
        /// Whether runtime ray tracing should be enabled independently of the graphics preset.
        /// </summary>
        [JsonPropertyName("RayTracingEnabled")]
        public bool RayTracingEnabled { get; set; }
    }
}
