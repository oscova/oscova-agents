using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for selecting clothing for an avatar slot.
    /// </summary>
    public class AvatarClothingSelectRequest
    {
        /// <summary>The avatar receiving the clothing selection.</summary>
        [JsonPropertyName("AvatarName")]
        public string AvatarName { get; set; } = string.Empty;

        /// <summary>The clothing slot, currently <c>top</c> or <c>bottom</c>.</summary>
        [JsonPropertyName("Slot")]
        public string Slot { get; set; } = string.Empty;

        /// <summary>The clothing identifier returned for the avatar and slot.</summary>
        [JsonPropertyName("ClothingId")]
        public string ClothingId { get; set; } = string.Empty;
    }
}
