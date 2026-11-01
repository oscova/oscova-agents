using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// An outfit option available for one avatar.
    /// </summary>
    public class AvatarOutfitOption
    {
        /// <summary>The stable outfit identifier used for selection.</summary>
        [JsonPropertyName("OutfitId")]
        public string OutfitId { get; set; } = string.Empty;

        /// <summary>The user-facing outfit name.</summary>
        [JsonPropertyName("DisplayName")]
        public string DisplayName { get; set; } = string.Empty;
    }
}
