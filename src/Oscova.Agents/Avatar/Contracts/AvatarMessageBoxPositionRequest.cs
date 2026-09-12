using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request sent to move the avatar message box while preserving its size.
    /// </summary>
    public class AvatarMessageBoxPositionRequest
    {
        /// <summary>
        /// Horizontal message box position as a screen fraction from 0.0 through 1.0.
        /// </summary>
        [JsonPropertyName("PositionX")]
        public float PositionX { get; set; }

        /// <summary>
        /// Vertical message box position as a screen fraction from 0.0 through 1.0.
        /// </summary>
        [JsonPropertyName("PositionY")]
        public float PositionY { get; set; }
    }
}
