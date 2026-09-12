using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for health/status check requests.
    /// </summary>
    public class AvatarHealthCheckResponse
    {
        /// <summary>
        /// Indicates whether the health check was successful.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// The current status of the avatar engine (e.g., "running").
        /// </summary>
        [JsonPropertyName("Status")]
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// The version of the avatar engine.
        /// </summary>
        [JsonPropertyName("Version")]
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// The port number the avatar engine is running on.
        /// </summary>
        [JsonPropertyName("Port")]
        public int Port { get; set; }
    }
}
