using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for background color change requests.
    /// </summary>
    public class AvatarBackgroundColorResponse : IAvatarBridgeResponse
    {
        /// <summary>
        /// Indicates whether the background color change was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The applied red component (0-255).
        /// </summary>
        [JsonPropertyName("Red")]
        public int Red { get; set; }

        /// <summary>
        /// The applied green component (0-255).
        /// </summary>
        [JsonPropertyName("Green")]
        public int Green { get; set; }

        /// <summary>
        /// The applied blue component (0-255).
        /// </summary>
        [JsonPropertyName("Blue")]
        public int Blue { get; set; }
    }
}
