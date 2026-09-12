using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for queueing speech playback from a local file.
    /// </summary>
    public class AvatarSpeechQueueFileRequest
    {
        /// <summary>
        /// The audio file name to play (e.g., "speech_001.wav").
        /// </summary>
        [JsonPropertyName("FileName")]
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// The text message corresponding to the speech.
        /// </summary>
        [JsonPropertyName("Message")]
        public string Message { get; set; } = string.Empty;
    }
}
