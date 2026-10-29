using Oscova.Agents.Speech;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Provides default speech provider configurations used when a user has not customized speech yet.
/// </summary>
public static class SpeechProviderConfigPresets
{
    public const string LocalRecognitionName = "Local Whisper";
    public const string LocalSynthesisName = "Local Kokoro";
    public const string OpenAiRecognitionName = "OpenAI Speech Recognition";
    public const string OpenAiSynthesisName = "OpenAI Speech Synthesis";
    /// <summary>Creates recognition templates with a local provider first when installed.</summary>
    public static IReadOnlyList<SpeechProviderConfig> GetRecognitionPresets(ISpeechProviderCatalog catalog) =>
        catalog.Recognition.OrderBy(provider => provider.Type != SpeechProviderKeys.Local)
            .Select(provider => new SpeechProviderConfig
            {
                Name = provider.Type switch
                {
                    SpeechProviderKeys.Local => LocalRecognitionName,
                    SpeechProviderKeys.OpenAI => OpenAiRecognitionName,
                    _ => provider.Type
                },
                Type = provider.Type,
                ModelId = provider.DefaultModel
            }).ToArray();

    /// <summary>Creates synthesis templates with defaults owned by each installed provider.</summary>
    public static IReadOnlyList<SpeechProviderConfig> GetSynthesisPresets(ISpeechProviderCatalog catalog) =>
        catalog.Synthesis.OrderBy(provider => provider.Type != SpeechProviderKeys.Local)
            .Select(provider => new SpeechProviderConfig
            {
                Name = provider.Type switch
                {
                    SpeechProviderKeys.Local => LocalSynthesisName,
                    SpeechProviderKeys.OpenAI => OpenAiSynthesisName,
                    _ => provider.Type
                },
                Type = provider.Type,
                ModelId = provider.DefaultModel,
                VoiceId = provider.DefaultVoiceId
            }).ToArray();
}
