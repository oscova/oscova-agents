using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for selecting outfit for an avatar.
    /// </summary>
    public class AvatarOutfitSelectRequest
    {
        /// <summary>The avatar receiving the outfit selection.</summary>
        [JsonPropertyName("AvatarName")]
        public string AvatarName { get; set; } = string.Empty;

        /// <summary>The outfit identifier returned for the avatar.</summary>
        [JsonPropertyName("OutfitId")]
        public string OutfitId { get; set; } = string.Empty;
    }
}
