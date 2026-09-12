using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for the avatar names reported by the running engine.
    /// </summary>
    public class AvatarListResponse
    {
        /// <summary>
        /// Indicates whether the list request was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Avatar names reported by the running engine.
        /// </summary>
        [JsonPropertyName("Avatars")]
        public IReadOnlyList<string> Avatars { get; set; } = [];
    }
}
