using Oscova.Agents.Avatar.Contracts;
using Oscova.Agents.Host.Constants;

namespace Oscova.Agents.Host.Services;

public class AvatarBridgeClient(HttpClient http, ILogger<AvatarBridgeClient> logger) : IAvatarBridgeClient
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetEmotionsAsync()
    {
        try
        {
            var result = await http.GetFromJsonAsync<AvatarEmotionListResponse>(ApiRoutes.AvatarBridge.Emotions).ConfigureAwait(false);
            return result?.Success == true ? result.Emotions : [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load avatar emotions via avatar bridge.");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task SetEmotionAsync(string emotionName, float blendSeconds)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                ApiRoutes.AvatarBridge.EmotionsSet,
                new AvatarEmotionRequest { EmotionName = emotionName, BlendSpeed = blendSeconds }).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Avatar bridge set emotion failed: {StatusCode}", response.StatusCode);
                return;
            }

            var result = await response.Content.ReadFromJsonAsync<AvatarEmotionResponse>().ConfigureAwait(false);
            if (result?.Success != true)
            {
                logger.LogWarning("Avatar bridge rejected emotion {EmotionName}: {Description}", emotionName, result?.Description);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to set avatar emotion {EmotionName} via avatar bridge.", emotionName);
        }
    }

    public async Task<bool> NotifySpeechQueueAsync(string audioUrl, string message, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            return false;
        }

        try
        {
            using var response = await http.PostAsJsonAsync(
                $"{ApiRoutes.AvatarBridge.Speech.Queue}?baseUrl={Uri.EscapeDataString(baseUrl)}",
                new AvatarSpeechQueueRequest { Url = audioUrl, Message = message }).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Avatar bridge notification failed: {StatusCode}", response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<AvatarSpeechQueueResponse>().ConfigureAwait(false);
            if (result?.Success == true)
            {
                return true;
            }

            logger.LogWarning("Avatar bridge notification was rejected: {Description}", result?.Description);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify avatar bridge for audio playback: {AudioUrl}.", audioUrl);
            return false;
        }
    }

    public async Task StopSpeechAsync(string baseUrl)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"{ApiRoutes.AvatarBridge.Speech.Stop}?baseUrl={Uri.EscapeDataString(baseUrl)}",
                new { ForcedStop = false }).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Avatar bridge stop speech failed: {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stop avatar speech via avatar bridge.");
        }
    }
}
