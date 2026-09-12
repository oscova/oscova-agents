using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for adjusting camera yaw (rotation around vertical axis).
    /// </summary>
    public class AvatarCameraYawRequest
    {
        /// <summary>
        /// Engine yaw value from -5.0 through 5.0. Negative values rotate the camera left, positive values rotate right.
        /// The engine does not range-check this value.
        /// </summary>
        [JsonPropertyName("Yaw")]
        public float Yaw { get; set; }
    }
}
