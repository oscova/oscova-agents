using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for selecting an avatar by name.
    /// </summary>
    public class AvatarSelectRequest
    {
        /// <summary>
        /// The name of the avatar to select (case-sensitive). Must match one of the available avatars.
        /// </summary>
        [JsonPropertyName("AvatarName")]
        public string AvatarName { get; set; } = string.Empty;
    }
}
