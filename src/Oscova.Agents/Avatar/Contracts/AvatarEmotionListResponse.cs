using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for the emotion names reported by the running engine.
    /// </summary>
    public class AvatarEmotionListResponse
    {
        /// <summary>
        /// Indicates whether the emotion list request was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Emotion names reported by the running engine.
        /// </summary>
        [JsonPropertyName("Emotions")]
        public IReadOnlyList<string> Emotions { get; set; } = [];
    }
}
