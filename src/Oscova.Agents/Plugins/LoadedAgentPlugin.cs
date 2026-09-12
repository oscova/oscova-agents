using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Oscova.Agents.Credentials;
using Oscova.Agents.Llm;
using Oscova.Agents.Speech;

namespace Oscova.Agents.Plugins;

/// <summary>
/// Runtime representation of a successfully loaded plugin.
/// </summary>
/// <param name="manifest">The validated manifest used to load the plugin.</param>
/// <param name="assembly">The loaded plugin assembly.</param>
/// <param name="pluginDirectory">The validated directory containing the plugin manifest.</param>
/// <param name="toolsets">The toolset contributions registered by the plugin entry point.</param>
/// <param name="modelProviders">The model provider contributions registered by the plugin entry point.</param>
/// <param name="speechRecognitionProviders">The speech recognition provider contributions registered by the plugin entry point.</param>
/// <param name="speechSynthesisProviders">The speech synthesis provider contributions registered by the plugin entry point.</param>
public sealed class LoadedAgentPlugin(
    AgentPluginManifest manifest,
    Assembly assembly,
    string pluginDirectory,
    IReadOnlyList<AgentToolsetContribution> toolsets,
    IReadOnlyList<AgentProviderContribution<IModelProvider>> modelProviders,
    IReadOnlyList<AgentProviderContribution<ISpeechRecognitionProvider>> speechRecognitionProviders,
    IReadOnlyList<AgentProviderContribution<ISpeechSynthesisProvider>> speechSynthesisProviders)
{
    /// <summary>
    /// Gets the validated manifest used to load the plugin.
    /// </summary>
    public AgentPluginManifest Manifest { get; } = manifest;

    /// <summary>
    /// Gets the loaded plugin assembly.
    /// </summary>
    public Assembly Assembly { get; } = assembly;

    /// <summary>Gets the validated directory containing this plugin's manifest.</summary>
    public string PluginDirectory { get; } = pluginDirectory;

    /// <summary>
    /// Gets the toolset contributions registered by the plugin entry point.
    /// </summary>
    public IReadOnlyList<AgentToolsetContribution> Toolsets { get; } = toolsets;

    /// <summary>Gets the model provider contributions registered by the plugin entry point.</summary>
    public IReadOnlyList<AgentProviderContribution<IModelProvider>> ModelProviders { get; } = modelProviders;

    /// <summary>Gets the speech recognition provider contributions registered by the plugin entry point.</summary>
    public IReadOnlyList<AgentProviderContribution<ISpeechRecognitionProvider>> SpeechRecognitionProviders { get; } = speechRecognitionProviders;

    /// <summary>Gets the speech synthesis provider contributions registered by the plugin entry point.</summary>
    public IReadOnlyList<AgentProviderContribution<ISpeechSynthesisProvider>> SpeechSynthesisProviders { get; } = speechSynthesisProviders;

    /// <summary>Gets the credential keys declared directly by this plugin's registered contributions.</summary>
    public IReadOnlySet<string> DeclaredCredentialKeys { get; } = toolsets
        .Select(contribution => contribution.ToolsetType)
        .Concat(modelProviders.Select(contribution => contribution.ProviderType))
        .Concat(speechRecognitionProviders.Select(contribution => contribution.ProviderType))
        .Concat(speechSynthesisProviders.Select(contribution => contribution.ProviderType))
        .Select(type => type.GetCustomAttribute<CredentialSlotAttribute>(inherit: false)?.Key)
        .Where(key => !string.IsNullOrWhiteSpace(key))
        .Select(key => key!.Trim())
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
