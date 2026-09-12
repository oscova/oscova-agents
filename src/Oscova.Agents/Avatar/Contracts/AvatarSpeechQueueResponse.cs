using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for remote-URL speech queue requests.
    /// </summary>
    public class AvatarSpeechQueueResponse
    {
        /// <summary>
        /// Indicates whether the speech queue request was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The local file path saved by the avatar bridge.
        /// </summary>
        [JsonPropertyName("SavedFilePath")]
        public string SavedFilePath { get; set; } = string.Empty;
    }
}
