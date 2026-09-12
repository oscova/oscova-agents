using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for hair options available to an avatar.
    /// </summary>
    public class AvatarHairListResponse : IAvatarBridgeResponse
    {
        /// <summary>Indicates whether the list request succeeded.</summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>Describes the operation result.</summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>The avatar whose hair options were requested.</summary>
        [JsonPropertyName("AvatarName")]
        public string AvatarName { get; set; } = string.Empty;

        /// <summary>The hair options available for the avatar.</summary>
        [JsonPropertyName("HairOptions")]
        public IReadOnlyList<AvatarHairOption> HairOptions { get; set; } = [];
    }
}
