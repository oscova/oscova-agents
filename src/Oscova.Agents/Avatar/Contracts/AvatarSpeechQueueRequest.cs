using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for queueing speech playback from a remote audio URL.
    /// </summary>
    public class AvatarSpeechQueueRequest
    {
        /// <summary>
        /// The remote audio URL to download and play.
        /// </summary>
        [JsonPropertyName("Url")]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// The text message corresponding to the speech.
        /// </summary>
        [JsonPropertyName("Message")]
        public string Message { get; set; } = string.Empty;
    }
}
