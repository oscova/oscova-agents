using System.Text.Json.Serialization;

namespace Oscova.Agents.Avatar.Contracts
{
    /// <summary>
    /// Request model for changing the avatar message font size.
    /// </summary>
    public class AvatarMessageFontSizeRequest
    {
        /// <summary>
        /// Message font size accepted by the avatar bridge, from 8 through 120.
        /// </summary>
        [JsonPropertyName("FontSize")]
        public int FontSize { get; set; }
    }
}
