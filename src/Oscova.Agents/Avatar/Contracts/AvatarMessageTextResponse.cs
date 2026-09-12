using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Response returned after showing standalone avatar message text.
    /// </summary>
    public class AvatarMessageTextResponse
    {
        /// <summary>Gets or sets whether the request succeeded.</summary>
        [JsonPropertyName("Success")]
        public bool Success { get; set; }
        /// <summary>Gets or sets the operation result.</summary>
        [JsonPropertyName("Description")]
        public string Description { get; set; } = string.Empty;
        /// <summary>Gets or sets the displayed message.</summary>
        [JsonPropertyName("Message")]
        public string Message { get; set; } = string.Empty;
    }
}
