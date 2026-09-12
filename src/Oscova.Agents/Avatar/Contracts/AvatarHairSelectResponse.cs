using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response model for avatar hair selection requests.
    /// </summary>
    public class AvatarHairSelectResponse : IAvatarBridgeResponse
    {
        /// <summary>Indicates whether the hair selection was accepted.</summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }

        /// <summary>Describes the operation result.</summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>The avatar that received the selection.</summary>
        [JsonPropertyName("SelectedAvatar")]
        public string SelectedAvatar { get; set; } = string.Empty;

        /// <summary>The accepted hair identifier.</summary>
        [JsonPropertyName("SelectedHair")]
        public string SelectedHair { get; set; } = string.Empty;
    }
}
