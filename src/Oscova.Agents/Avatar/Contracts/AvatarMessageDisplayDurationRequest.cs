using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for changing avatar message display duration.
    /// </summary>
    public class AvatarMessageDisplayDurationRequest
    {
        /// <summary>
        /// Message display duration in seconds. The avatar bridge accepts 0.0 or greater.
        /// </summary>
        [JsonPropertyName("DurationSeconds")]
        public float DurationSeconds { get; set; }
    }
}
