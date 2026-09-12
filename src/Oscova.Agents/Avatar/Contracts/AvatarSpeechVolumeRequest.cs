using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for setting speech playback volume.
    /// </summary>
    public class AvatarSpeechVolumeRequest
    {
        /// <summary>
        /// Volume level between 0.0 (muted) and 1.0 (full volume). Values outside this range are rejected by the avatar engine.
        /// Note: Setting volume to 0.0 will disable lip sync. For lip sync to work, the volume must be greater than zero (recommended: 0.01).
        /// </summary>
        [JsonPropertyName("Volume")]
        public float Volume { get; set; }
    }
}
