using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for speech stop requests.
    /// Stops both audio playback and lip sync animation.
    /// </summary>
    public class AvatarSpeechStopResponse
    {
        /// <summary>
        /// Indicates whether the stop request was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Indicates whether speech was stopped immediately.
        /// </summary>
        [JsonPropertyName("Stopped")]
        public bool Stopped { get; set; }
    }
}
