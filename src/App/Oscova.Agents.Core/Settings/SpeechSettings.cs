using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;
using Oscova.Agents.Core.Speech;
using Oscova.Agents.Speech;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Speech-related settings for provider selection, voice selection, and admin playback routing.
/// </summary>
public class SpeechSettings : SettingsBase
{
    public const string Key = "speech";
    public const string DefaultVoiceId = "af_heart";
    private ObservableCollection<SpeechProviderConfig> _recognitionProviders = [];
    private ObservableCollection<SpeechProviderConfig> _synthesisProviders = [];
    private readonly IReadOnlyList<SpeechProviderConfig> _recognitionPresets;
    private readonly IReadOnlyList<SpeechProviderConfig> _synthesisPresets;
    private Guid? _selectedRecognitionProviderId;
    private Guid? _selectedSynthesisProviderId;
    private string _selectedVoiceId = DefaultVoiceId;
    private SpeechOutputMode _assistantSpeechOutputMode = SpeechOutputMode.BrowserOnly;

    public SpeechSettings(IAppSettingsRepository repository, string userId, ILoggerFactory loggerFactory, ISpeechProviderCatalog catalog)
        : base(repository, userId, loggerFactory)
    {
        _recognitionPresets = SpeechProviderConfigPresets.GetRecognitionPresets(catalog);
        _synthesisPresets = SpeechProviderConfigPresets.GetSynthesisPresets(catalog);
        _recognitionProviders.CollectionChanged += ProvidersOnCollectionChanged;
        _synthesisProviders.CollectionChanged += ProvidersOnCollectionChanged;
        EnsureDefaults();
        _selectedVoiceId = ResolveSelectedSynthesisVoice();
    }

    public override string SettingsKey => Key;

    public ObservableCollection<SpeechProviderConfig> RecognitionProviderConfigs
    {
        get => _recognitionProviders;
        set
        {
            if (ReferenceEquals(_recognitionProviders, value))
            {
                return;
            }

            _recognitionProviders.CollectionChanged -= ProvidersOnCollectionChanged;
            _recognitionProviders = value ?? [];
            _recognitionProviders.CollectionChanged += ProvidersOnCollectionChanged;
            EnsureDefaults();
            OnPropertyChanged();
            OnChanged();
        }
    }

    public ObservableCollection<SpeechProviderConfig> SynthesisProviderConfigs
    {
        get => _synthesisProviders;
        set
        {
            if (ReferenceEquals(_synthesisProviders, value))
            {
                return;
            }

            _synthesisProviders.CollectionChanged -= ProvidersOnCollectionChanged;
            _synthesisProviders = value ?? [];
            _synthesisProviders.CollectionChanged += ProvidersOnCollectionChanged;
            EnsureDefaults();
            OnPropertyChanged();
            OnChanged();
        }
    }

    public Guid? SelectedRecognitionProviderId
    {
        get => _selectedRecognitionProviderId;
        set => SetProperty(ref _selectedRecognitionProviderId, value);
    }

    public Guid? SelectedSynthesisProviderId
    {
        get => _selectedSynthesisProviderId;
        set => SetProperty(ref _selectedSynthesisProviderId, value);
    }

    public SpeechProviderConfig? SelectedRecognitionProvider =>
        _selectedRecognitionProviderId.HasValue
            ? _recognitionProviders.FirstOrDefault(provider => provider.Id == _selectedRecognitionProviderId.Value)
            : null;

    public SpeechProviderConfig? SelectedSynthesisProvider =>
        _selectedSynthesisProviderId.HasValue
            ? _synthesisProviders.FirstOrDefault(provider => provider.Id == _selectedSynthesisProviderId.Value)
            : null;

    public string SelectedVoiceId
    {
        get => _selectedVoiceId;
        set => SetProperty(ref _selectedVoiceId, string.IsNullOrWhiteSpace(value) ? DefaultVoiceId : value);
    }

    public SpeechOutputMode AssistantSpeechOutputMode
    {
        get => _assistantSpeechOutputMode;
        set => SetProperty(ref _assistantSpeechOutputMode, value);
    }

    public override string ToYaml()
    {
        return Yaml.Serialize(new Snapshot
        {
            RecognitionProviders = [.. RecognitionProviderConfigs.Select(ToFile)],
            SynthesisProviders = [.. SynthesisProviderConfigs.Select(ToFile)],
            SelectedRecognitionProviderId = SelectedRecognitionProviderId,
            SelectedSynthesisProviderId = SelectedSynthesisProviderId,
            SelectedVoiceId = SelectedVoiceId,
            AssistantSpeechOutputMode = AssistantSpeechOutputMode
        });
    }

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        if (data is null)
        {
            return;
        }

        RecognitionProviderConfigs = data.RecognitionProviders is null
            ? new ObservableCollection<SpeechProviderConfig>(_recognitionPresets.Select(provider => provider.Clone()))
            : new ObservableCollection<SpeechProviderConfig>(data.RecognitionProviders.Select(ToConfig));

        SynthesisProviderConfigs = data.SynthesisProviders is null
            ? new ObservableCollection<SpeechProviderConfig>(_synthesisPresets.Select(provider => provider.Clone()))
            : new ObservableCollection<SpeechProviderConfig>(data.SynthesisProviders.Select(ToConfig));

        SelectedRecognitionProviderId = data.SelectedRecognitionProviderId.HasValue &&
            RecognitionProviderConfigs.Any(provider => provider.Id == data.SelectedRecognitionProviderId.Value)
                ? data.SelectedRecognitionProviderId
                : RecognitionProviderConfigs.FirstOrDefault()?.Id;

        SelectedSynthesisProviderId = data.SelectedSynthesisProviderId.HasValue &&
            SynthesisProviderConfigs.Any(provider => provider.Id == data.SelectedSynthesisProviderId.Value)
                ? data.SelectedSynthesisProviderId
                : SynthesisProviderConfigs.FirstOrDefault()?.Id;

        SelectedVoiceId = string.IsNullOrWhiteSpace(data.SelectedVoiceId) ? ResolveSelectedSynthesisVoice() : data.SelectedVoiceId;
        AssistantSpeechOutputMode = Enum.IsDefined(data.AssistantSpeechOutputMode)
            ? data.AssistantSpeechOutputMode
            : SpeechOutputMode.BrowserOnly;
    }

    private void EnsureDefaults()
    {
        AddMissingPresets(_recognitionProviders, _recognitionPresets);
        AddMissingPresets(_synthesisProviders, _synthesisPresets);

        if (_selectedRecognitionProviderId is null || !_recognitionProviders.Any(provider => provider.Id == _selectedRecognitionProviderId))
        {
            _selectedRecognitionProviderId = _recognitionProviders.FirstOrDefault()?.Id;
        }

        if (_selectedSynthesisProviderId is null || !_synthesisProviders.Any(provider => provider.Id == _selectedSynthesisProviderId))
        {
            _selectedSynthesisProviderId = _synthesisProviders.FirstOrDefault()?.Id;
        }
    }

    private string ResolveSelectedSynthesisVoice()
    {
        return SelectedSynthesisProvider?.VoiceId ?? DefaultVoiceId;
    }

    private void ProvidersOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        EnsureDefaults();
        OnChanged();
    }

    private static void AddMissingPresets(
        ObservableCollection<SpeechProviderConfig> providers,
        IReadOnlyList<SpeechProviderConfig> presets)
    {
        foreach (var preset in presets)
        {
            if (providers.Any(provider => provider.Type == preset.Type))
            {
                continue;
            }

            providers.Add(preset.Clone());
        }
    }

    private static SpeechProviderConfigFile ToFile(SpeechProviderConfig config)
    {
        return new SpeechProviderConfigFile
        {
            Id = config.Id,
            Name = config.Name,
            Type = config.Type,
            BaseUrl = config.BaseUrl,
            ModelId = config.ModelId,
            CredentialKey = config.CredentialKey,
            VoiceId = config.VoiceId
        };
    }

    private static SpeechProviderConfig ToConfig(SpeechProviderConfigFile file)
    {
        return new SpeechProviderConfig
        {
            Id = file.Id == Guid.Empty ? Guid.NewGuid() : file.Id,
            Name = file.Name,
            Type = file.Type,
            BaseUrl = file.BaseUrl,
            ModelId = file.ModelId,
            CredentialKey = file.CredentialKey,
            VoiceId = file.VoiceId
        };
    }

    private sealed class Snapshot
    {
        public List<SpeechProviderConfigFile>? RecognitionProviders { get; set; }
        public List<SpeechProviderConfigFile>? SynthesisProviders { get; set; }
        public Guid? SelectedRecognitionProviderId { get; set; }
        public Guid? SelectedSynthesisProviderId { get; set; }
        public string? SelectedVoiceId { get; set; }
        public SpeechOutputMode AssistantSpeechOutputMode { get; set; } = SpeechOutputMode.BrowserOnly;
    }

    private sealed class SpeechProviderConfigFile
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = SpeechProviderKeys.Local;
        public string? BaseUrl { get; set; }
        public string? ModelId { get; set; }
        public string? CredentialKey { get; set; }
        public string? VoiceId { get; set; }
    }
}
