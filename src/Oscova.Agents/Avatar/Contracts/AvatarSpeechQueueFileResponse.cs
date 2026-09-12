using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for local-file speech queue requests.
    /// </summary>
    public class AvatarSpeechQueueFileResponse
    {
        /// <summary>
        /// Indicates whether the speech request was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;
    }
}
