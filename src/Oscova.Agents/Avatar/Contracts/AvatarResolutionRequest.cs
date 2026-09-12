using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for changing the engine/window resolution and fullscreen mode.
    /// </summary>
    public class AvatarResolutionRequest
    {
        /// <summary>
        /// Window width in pixels.
        /// </summary>
        [JsonPropertyName("Width")]
        public int Width { get; set; }

        /// <summary>
        /// Window height in pixels.
        /// </summary>
        [JsonPropertyName("Height")]
        public int Height { get; set; }

        /// <summary>
        /// Whether to enable fullscreen mode.
        /// </summary>
        [JsonPropertyName("Fullscreen")]
        public bool Fullscreen { get; set; }
    }
}
