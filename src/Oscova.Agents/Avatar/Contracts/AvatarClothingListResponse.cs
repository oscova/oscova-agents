using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for clothing options available to an avatar.
    /// </summary>
    public class AvatarClothingListResponse : IAvatarBridgeResponse
    {
        /// <summary>Indicates whether the list request succeeded.</summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>Describes the operation result.</summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>The avatar whose clothing options were requested.</summary>
        [JsonPropertyName("AvatarName")]
        public string AvatarName { get; set; } = string.Empty;

        /// <summary>The clothing options available for the avatar.</summary>
        [JsonPropertyName("ClothingOptions")]
        public IReadOnlyList<AvatarClothingOption> ClothingOptions { get; set; } = [];
    }
}
