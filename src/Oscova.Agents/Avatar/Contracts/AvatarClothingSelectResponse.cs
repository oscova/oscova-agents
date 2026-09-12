using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for avatar clothing selection requests.
    /// </summary>
    public class AvatarClothingSelectResponse : IAvatarBridgeResponse
    {
        /// <summary>Indicates whether the clothing selection was accepted.</summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>Describes the operation result.</summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>The avatar that received the selection.</summary>
        [JsonPropertyName("SelectedAvatar")]
        public string SelectedAvatar { get; set; } = string.Empty;

        /// <summary>The slot that received the selection.</summary>
        [JsonPropertyName("SelectedSlot")]
        public string SelectedSlot { get; set; } = string.Empty;

        /// <summary>The accepted clothing identifier.</summary>
        [JsonPropertyName("SelectedClothing")]
        public string SelectedClothing { get; set; } = string.Empty;
    }
}
