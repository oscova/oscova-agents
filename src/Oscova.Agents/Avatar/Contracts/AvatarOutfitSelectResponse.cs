using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for avatar outfit selection requests.
    /// </summary>
    public class AvatarOutfitSelectResponse : IAvatarBridgeResponse
    {
        /// <summary>Indicates whether the outfit selection was accepted.</summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>Describes the operation result.</summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>The avatar that received the selection.</summary>
        [JsonPropertyName("SelectedAvatar")]
        public string SelectedAvatar { get; set; } = string.Empty;

        /// <summary>The accepted outfit identifier.</summary>
        [JsonPropertyName("SelectedOutfit")]
        public string SelectedOutfit { get; set; } = string.Empty;
    }
}
