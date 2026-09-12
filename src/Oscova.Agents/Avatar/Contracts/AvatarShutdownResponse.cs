using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for shutdown requests.
    /// The response is sent before the engine terminates.
    /// </summary>
    public class AvatarShutdownResponse
    {
        /// <summary>
        /// Indicates whether the shutdown request was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Indicates whether the shutdown process has been initiated.
        /// </summary>
        [JsonPropertyName("ShutdownInitiated")]
        public bool ShutdownInitiated { get; set; }
    }
}
