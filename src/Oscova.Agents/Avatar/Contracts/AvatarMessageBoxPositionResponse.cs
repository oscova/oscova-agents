using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response returned after applying avatar message box position.
    /// </summary>
    public class AvatarMessageBoxPositionResponse : IAvatarBridgeResponse
    {
        /// <summary>Gets or sets whether the request succeeded.</summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }
        /// <summary>Gets or sets the operation result.</summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;
        /// <summary>Gets or sets the applied horizontal position.</summary>
        [JsonPropertyName("PositionX")]
        public float PositionX { get; set; }
        /// <summary>Gets or sets the applied vertical position.</summary>
        [JsonPropertyName("PositionY")]
        public float PositionY { get; set; }
    }
}
