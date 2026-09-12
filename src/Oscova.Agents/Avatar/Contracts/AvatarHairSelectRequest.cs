using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for selecting hair for an avatar.
    /// </summary>
    public class AvatarHairSelectRequest
    {
        /// <summary>The avatar receiving the hair selection.</summary>
        [JsonPropertyName("AvatarName")]
        public string AvatarName { get; set; } = string.Empty;

        /// <summary>The hair identifier returned for the avatar.</summary>
        [JsonPropertyName("HairId")]
        public string HairId { get; set; } = string.Empty;
    }
}
