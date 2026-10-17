using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for setting a target emotion with smooth transition.
    /// </summary>
    public class AvatarEmotionRequest
    {
        /// <summary>
        /// The name of the emotion to set (case-sensitive). Must match one of the available emotions.
        /// </summary>
        [JsonPropertyName("EmotionName")]
        public string EmotionName { get; set; } = string.Empty;

        /// <summary>
        /// Duration in seconds for the emotion transition. Must be greater than zero. A value of 0.25 is recommended.
        /// Lower values create faster transitions, higher values create smoother, more gradual changes.
        /// </summary>
        [JsonPropertyName("BlendSpeed")]
        public float BlendSpeed { get; set; }

        /// <summary>Finite emotion strength from 0 to 1, defaulting to full influence when omitted.</summary>
        [JsonPropertyName("Intensity")]
        public float Intensity { get; set; } = 1f;
    }
}
