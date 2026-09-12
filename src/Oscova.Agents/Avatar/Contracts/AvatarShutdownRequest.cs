using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for graceful shutdown of the avatar engine.
    /// </summary>
    public class AvatarShutdownRequest
    {
        /// <summary>
        /// If true, immediately terminates the engine. If false, allows graceful cleanup before shutdown.
        /// </summary>
        [JsonPropertyName("ForceShutdown")]
        public bool ForceShutdown { get; set; }
    }
}
