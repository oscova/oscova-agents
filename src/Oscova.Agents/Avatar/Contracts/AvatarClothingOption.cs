using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// A clothing option available for one avatar slot.
    /// </summary>
    public class AvatarClothingOption
    {
        /// <summary>The clothing slot, currently <c>top</c> or <c>bottom</c>.</summary>
        [JsonPropertyName("Slot")]
        public string Slot { get; set; } = string.Empty;

        /// <summary>The stable clothing identifier used for selection.</summary>
        [JsonPropertyName("ClothingId")]
        public string ClothingId { get; set; } = string.Empty;

        /// <summary>The user-facing clothing name.</summary>
        [JsonPropertyName("DisplayName")]
        public string DisplayName { get; set; } = string.Empty;
    }
}
