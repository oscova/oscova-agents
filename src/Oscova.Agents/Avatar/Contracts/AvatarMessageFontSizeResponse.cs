using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for avatar message font size changes.
    /// </summary>
    public class AvatarMessageFontSizeResponse : IAvatarBridgeResponse
    {
        /// <summary>
        /// Indicates whether the font size change was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The applied message font size.
        /// </summary>
        [JsonPropertyName("FontSize")]
        public int FontSize { get; set; }
    }
}
