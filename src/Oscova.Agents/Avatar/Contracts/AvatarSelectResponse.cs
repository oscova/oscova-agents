using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for avatar selection requests.
    /// Switching avatars may cause a brief loading period.
    /// </summary>
    public class AvatarSelectResponse : IAvatarBridgeResponse
    {
        /// <summary>
        /// Indicates whether the avatar selection was processed successfully.
        /// </summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable description of the operation result.
        /// </summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The name of the currently selected avatar.
        /// </summary>
        [JsonPropertyName("SelectedAvatar")]
        public string SelectedAvatar { get; set; } = string.Empty;
    }
}
