#if OPENAI_PLUGIN
namespace Oscova.Agents.Plugins.OpenAI.Settings;
#elif ELEVENLABS_PLUGIN
namespace Oscova.Agents.Plugins.ElevenLabs.Settings;
#elif LOCAL_RECOGNITION_PLUGIN
namespace Oscova.Agents.Plugins.LocalSpeechRecognition.Settings;
#elif LOCAL_SYNTHESIS_PLUGIN
namespace Oscova.Agents.Plugins.LocalSpeechSynthesis.Settings;
#endif

/// <summary>Identifies the embedded speech settings text used by the owning plugin.</summary>
public static class PluginTextKeys
{
    public const string SpeechCredentialLabel = "Settings.Speech.CredentialLabel";
    public const string SpeechCredentialPlaceholder = "Settings.Speech.CredentialPlaceholder";
    public const string SpeechCredentialHelp = "Settings.Speech.CredentialHelp";
    public const string SpeechModelLabel = "Settings.Speech.ModelLabel";
    public const string SpeechVoiceLabel = "Settings.Speech.VoiceLabel";
    public const string SpeechNoVoicesOption = "Settings.Speech.NoVoicesOption";
    public const string SpeechVoiceHelperText = "Settings.Speech.VoiceHelperText";
    public const string CredentialsMissingStatus = "Settings.Credentials.MissingStatus";
    public const string SpeechRecognitionSectionTitle = "Settings.Speech.RecognitionSectionTitle";
    public const string SpeechSynthesisSectionTitle = "Settings.Speech.SynthesisSectionTitle";
    public const string CancelDownloadButton = "Settings.Speech.CancelDownloadButton";
    public const string DownloadModelButton = "Settings.Speech.DownloadModelButton";
    public const string ModelReadyLabel = "Settings.Speech.ModelReadyLabel";
    public const string ModelMissingLabel = "Settings.Speech.ModelMissingLabel";
    public const string SkillsInstallStageDownloading = "Settings.Skills.InstallStageDownloading";
    public const string DownloadingLabel = "Settings.Speech.DownloadingLabel";
}
