using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for changing the background color.
    /// </summary>
    public class AvatarBackgroundColorRequest
    {
        /// <summary>
        /// Red component (0-255). Values outside this range are rejected by the avatar engine.
        /// </summary>
        [JsonPropertyName("Red")]
        public int Red { get; set; }

        /// <summary>
        /// Green component (0-255). Values outside this range are rejected by the avatar engine.
        /// </summary>
        [JsonPropertyName("Green")]
        public int Green { get; set; }

        /// <summary>
        /// Blue component (0-255). Values outside this range are rejected by the avatar engine.
        /// </summary>
        [JsonPropertyName("Blue")]
        public int Blue { get; set; }
    }
}
