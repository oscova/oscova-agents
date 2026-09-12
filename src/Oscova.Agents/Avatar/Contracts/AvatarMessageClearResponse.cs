using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response returned after clearing the avatar message panel.
    /// </summary>
    public class AvatarMessageClearResponse
    {
        /// <summary>Gets or sets whether the request succeeded.</summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }
        /// <summary>Gets or sets the operation result.</summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;
        /// <summary>Gets or sets whether the message was cleared.</summary>
        [JsonPropertyName("Cleared")]
        public bool Cleared { get; set; }
    }
}
