using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for changing avatar message color and opacity.
    /// </summary>
    public class AvatarMessageColorAndOpacityRequest
    {
        /// <summary>
        /// Normalized red component, from 0.0 through 1.0.
        /// </summary>
        [JsonPropertyName("Red")]
        public float Red { get; set; }

        /// <summary>
        /// Normalized green component, from 0.0 through 1.0.
        /// </summary>
        [JsonPropertyName("Green")]
        public float Green { get; set; }

        /// <summary>
        /// Normalized blue component, from 0.0 through 1.0.
        /// </summary>
        [JsonPropertyName("Blue")]
        public float Blue { get; set; }

        /// <summary>
        /// Normalized alpha component, from 0.0 through 1.0.
        /// </summary>
        [JsonPropertyName("Alpha")]
        public float Alpha { get; set; }
    }
}
