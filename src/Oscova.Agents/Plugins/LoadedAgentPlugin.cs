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
/// <param name="contributions">The plugin's registered contributions.</param>
public sealed class LoadedAgentPlugin(
    AgentPluginManifest manifest,
    Assembly assembly,
    string pluginDirectory,
    AgentPluginBuilder contributions)
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
    public IReadOnlyList<AgentToolsetContribution> Toolsets { get; } = contributions.Toolsets.ToArray();

    /// <summary>Gets the model provider contributions registered by the plugin entry point.</summary>
    public IReadOnlyList<AgentProviderContribution<IModelProvider>> ModelProviders { get; } = contributions.ModelProviders.ToArray();

    /// <summary>Gets the speech recognition provider contributions registered by the plugin entry point.</summary>
    public IReadOnlyList<AgentProviderContribution<ISpeechRecognitionProvider>> SpeechRecognitionProviders { get; } = contributions.SpeechRecognitionProviders.ToArray();

    /// <summary>Gets the speech synthesis provider contributions registered by the plugin entry point.</summary>
    public IReadOnlyList<AgentProviderContribution<ISpeechSynthesisProvider>> SpeechSynthesisProviders { get; } = contributions.SpeechSynthesisProviders.ToArray();

    /// <summary>Gets the credential keys declared directly by this plugin's registered contributions.</summary>
    public IReadOnlySet<string> DeclaredCredentialKeys => _attributeCredentialKeys
        .Concat(Credentials.Select(declaration => declaration.Key.Trim()))
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets channel contributions registered by the entry point.</summary>
    public IReadOnlyList<AgentChannelContribution> Channels { get; } = contributions.Channels.ToArray();

    /// <summary>Gets structured credential slots registered by the entry point.</summary>
    public IReadOnlyList<CredentialDeclaration> Credentials { get; } = contributions.Credentials.ToArray();

    /// <summary>Gets the OAuth account providers registered by the entry point.</summary>
    public IReadOnlyList<OAuthProviderDefinition> OAuthProviders { get; } = contributions.OAuthProviders.ToArray();

    /// <summary>Gets the agent starters registered by the entry point.</summary>
    public IReadOnlyList<AgentTemplateDefinition> AgentTemplates { get; } = contributions.AgentTemplates.ToArray();

    /// <summary>Gets task declarations registered by the entry point.</summary>
    public IReadOnlyList<AgentTaskContribution> Tasks { get; } = contributions.Tasks.ToArray();

    /// <summary>Gets the packaged default instructions declared by this plugin.</summary>
    public IReadOnlyList<AgentInstructionContribution> Instructions { get; } = contributions.Instructions.ToArray();

    /// <summary>Gets this plugin's display resource marker.</summary>
    public Type? ResourceType { get; } = contributions.ResourceType;

    /// <summary>Gets execution providers declared by this plugin.</summary>
    public IReadOnlyList<AgentExecutionContribution> ExecutionProviders { get; } = contributions.ExecutionProviders.ToArray();
    /// <summary>Gets container launchers declared by this plugin.</summary>
    public IReadOnlyList<AgentContainerLaunchContribution> ContainerLaunchers { get; } = contributions.ContainerLaunchers.ToArray();
    /// <summary>Gets compiled settings panels declared by this plugin.</summary>
    public IReadOnlyList<AgentSettingsContribution> SettingsPanels { get; } = contributions.SettingsPanels.ToArray();

    private readonly IReadOnlySet<string> _attributeCredentialKeys = contributions.Toolsets
        .Select(contribution => contribution.ToolsetType)
        .Concat(contributions.ModelProviders.Select(contribution => contribution.ProviderType))
        .Concat(contributions.SpeechRecognitionProviders.Select(contribution => contribution.ProviderType))
        .Concat(contributions.SpeechSynthesisProviders.Select(contribution => contribution.ProviderType))
        .Select(type => type.GetCustomAttribute<CredentialSlotAttribute>(inherit: false)?.Key)
        .Where(key => !string.IsNullOrWhiteSpace(key))
        .Select(key => key!.Trim())
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
