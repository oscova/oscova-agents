using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// A hair option available for one avatar.
    /// </summary>
    public class AvatarHairOption
    {
        /// <summary>The stable hair identifier used for selection.</summary>
        [JsonPropertyName("HairId")]
        public string HairId { get; set; } = string.Empty;

        /// <summary>The user-facing hair name.</summary>
        [JsonPropertyName("DisplayName")]
        public string DisplayName { get; set; } = string.Empty;
    }
}
