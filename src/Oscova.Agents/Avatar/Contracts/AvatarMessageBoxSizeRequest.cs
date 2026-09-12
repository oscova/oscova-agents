using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request sent to resize the avatar message box while preserving its position.
    /// </summary>
    public class AvatarMessageBoxSizeRequest
    {
        /// <summary>
        /// Message box width as a screen fraction greater than 0.0 through 1.0.
        /// </summary>
        [JsonPropertyName("Width")]
        public float Width { get; set; }

        /// <summary>
        /// Message box height as a screen fraction greater than 0.0 through 1.0.
        /// </summary>
        [JsonPropertyName("Height")]
        public float Height { get; set; }
    }
}
