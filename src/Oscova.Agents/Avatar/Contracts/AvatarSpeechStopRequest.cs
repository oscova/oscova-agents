using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for stopping speech playback and lip-sync animation.
    /// </summary>
    public class AvatarSpeechStopRequest
    {
        /// <summary>
        /// True to interrupt immediately. False to stop gracefully (recommended).
        /// </summary>
        [JsonPropertyName("ForcedStop")]
        public bool ForcedStop { get; set; }
    }
}
