using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// System-wide settings that govern URLs and file names used when downloading
/// optional models (speech recognition, speech synthesis, default local LLM).
/// Edited by administrators via the settings file; there is no in-app UI for these yet.
/// </summary>
public sealed class ModelDownloadSettings : SettingsBase
{
    public const string Key = "model-downloads";

    private string _asrModelUrl = string.Empty;
    private string _ttsModelUrl = string.Empty;
    private string _defaultLocalModelUrl = "https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF/resolve/main/gemma-4-E2B-it-Q8_0.gguf";
    private string _defaultLocalModelFileName = "gemma-4-E2B-it-Q8_0.gguf";

    public ModelDownloadSettings(IAppSettingsRepository repository, ILoggerFactory loggerFactory)
        : base(repository, WellKnownUsers.SystemId, loggerFactory) { }

    public override string SettingsKey => Key;

    // Empty falls back to the hard-coded URL on the speech service.
    public string AsrModelUrl
    {
        get => _asrModelUrl;
        set => SetProperty(ref _asrModelUrl, value ?? string.Empty);
    }

    public string TtsModelUrl
    {
        get => _ttsModelUrl;
        set => SetProperty(ref _ttsModelUrl, value ?? string.Empty);
    }

    // When empty the onboarding wizard's Local tab shows a "not configured" notice.
    public string DefaultLocalModelUrl
    {
        get => _defaultLocalModelUrl;
        set => SetProperty(ref _defaultLocalModelUrl, value ?? string.Empty);
    }

    // Bare file name (not a full path). Stamped onto the seeded Local provider config.
    public string DefaultLocalModelFileName
    {
        get => _defaultLocalModelFileName;
        set => SetProperty(ref _defaultLocalModelFileName, value ?? string.Empty);
    }

    public override string ToYaml()
    {
        return Yaml.Serialize(new Snapshot
        {
            AsrModelUrl = AsrModelUrl,
            TtsModelUrl = TtsModelUrl,
            DefaultLocalModelUrl = DefaultLocalModelUrl,
            DefaultLocalModelFileName = DefaultLocalModelFileName
        });
    }

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        if (data is null)
        {
            return;
        }

        AsrModelUrl = data.AsrModelUrl ?? string.Empty;
        TtsModelUrl = data.TtsModelUrl ?? string.Empty;
        DefaultLocalModelUrl = data.DefaultLocalModelUrl ?? string.Empty;
        DefaultLocalModelFileName = data.DefaultLocalModelFileName ?? string.Empty;
    }

    private sealed class Snapshot
    {
        public string? AsrModelUrl { get; set; }
        public string? TtsModelUrl { get; set; }
        public string? DefaultLocalModelUrl { get; set; }
        public string? DefaultLocalModelFileName { get; set; }
    }
}
