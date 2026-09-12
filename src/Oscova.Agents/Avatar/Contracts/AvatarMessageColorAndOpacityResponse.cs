using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for avatar message color and opacity changes.
    /// </summary>
    public class AvatarMessageColorAndOpacityResponse : IAvatarBridgeResponse
    {
        /// <summary>
        /// Indicates whether the message color change was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The applied normalized red component.
        /// </summary>
        [JsonPropertyName("Red")]
        public float Red { get; set; }

        /// <summary>
        /// The applied normalized green component.
        /// </summary>
        [JsonPropertyName("Green")]
        public float Green { get; set; }

        /// <summary>
        /// The applied normalized blue component.
        /// </summary>
        [JsonPropertyName("Blue")]
        public float Blue { get; set; }

        /// <summary>
        /// The applied normalized alpha component.
        /// </summary>
        [JsonPropertyName("Alpha")]
        public float Alpha { get; set; }
    }
}
