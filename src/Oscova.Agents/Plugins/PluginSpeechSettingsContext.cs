using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Oscova.Agents.Speech;
using Oscova.Agents.Speech.Contracts;

namespace Oscova.Agents.Plugins;

/// <summary>Supplies a selected provider's unsaved speech configuration without credential values.</summary>
public sealed record PluginSpeechSettingsContext(SpeechProviderConfig Draft, string? DefaultModel,
    IReadOnlyList<PluginSpeechCredential> Credentials, IReadOnlyList<SpeechSynthesisVoice> Voices,
    bool VoiceSelectionDisabled, Func<Task> ChangedAsync, Func<string, Task> SelectVoiceAsync,
    PluginSpeechModelDownload? ModelDownload);

/// <summary>Describes a saved credential selection without exposing its secret values.</summary>
public sealed record PluginSpeechCredential(string Key, string Alias, bool IsSet);

/// <summary>Supplies admitted model download operations and status notifications.</summary>
public sealed record PluginSpeechModelDownload(string? Title, Func<Task<PluginSpeechDownloadStatus>> ReadAsync,
    Func<Task> StartAsync, Func<Task> CancelAsync, Func<Action, IDisposable> Subscribe);

/// <summary>Describes the current model readiness and download progress.</summary>
public sealed record PluginSpeechDownloadStatus(bool Ready, bool Downloading, double? Percent);
