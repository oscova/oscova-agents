using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for emotion set requests.
    /// The emotion will smoothly transition over the specified blend speed duration.
    /// </summary>
    public class AvatarEmotionResponse : IAvatarBridgeResponse
    {
        /// <summary>
        /// Indicates whether the emotion set request was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The name of the emotion that was selected.
        /// </summary>
        [JsonPropertyName("SelectedEmotion")]
        public string SelectedEmotion { get; set; } = string.Empty;

        /// <summary>
        /// The applied blend speed in seconds for the emotion transition.
        /// </summary>
        [JsonPropertyName("BlendSpeed")]
        public float BlendSpeed { get; set; }
    }
}
