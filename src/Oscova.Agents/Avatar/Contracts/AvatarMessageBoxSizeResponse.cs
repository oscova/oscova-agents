using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response returned after applying avatar message box size.
    /// </summary>
    public class AvatarMessageBoxSizeResponse : IAvatarBridgeResponse
    {
        /// <summary>Gets or sets whether the request succeeded.</summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }
        /// <summary>Gets or sets the operation result.</summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;
        /// <summary>Gets or sets the applied width.</summary>
        [JsonPropertyName("Width")]
        public float Width { get; set; }
        /// <summary>Gets or sets the applied height.</summary>
        [JsonPropertyName("Height")]
        public float Height { get; set; }
    }
}
