namespace Oscova.Agents.Host.Services;

public interface IAvatarBridgeClient
{
    /// <summary>Returns the connected Engine's emotion names in authored order, or an empty list.</summary>
    Task<IReadOnlyList<string>> GetEmotionsAsync();

    /// <summary>Blends the avatar to an emotion over the given seconds.</summary>
    Task SetEmotionAsync(string emotionName, float blendSeconds);

    Task<bool> NotifySpeechQueueAsync(string audioUrl, string message, string baseUrl);
    Task StopSpeechAsync(string baseUrl);
}
