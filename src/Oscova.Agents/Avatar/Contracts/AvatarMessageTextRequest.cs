using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request sent to show text on the avatar message panel without audio playback.
    /// </summary>
    public class AvatarMessageTextRequest
    {
        /// <summary>Gets or sets the message to display.</summary>
        [JsonPropertyName("Message")]
        public string Message { get; set; } = string.Empty;
    }
}
