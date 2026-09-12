using System;
using System.Collections.Generic;
using Oscova.Agents.Llm;
using Oscova.Agents.Speech;

namespace Oscova.Agents.Plugins;

/// <summary>
/// Collects contributions declared by a plugin entry point.
/// </summary>
public sealed class AgentPluginBuilder
{
    private readonly List<AgentToolsetContribution> _toolsets = [];
    private readonly List<AgentProviderContribution<IModelProvider>> _modelProviders = [];
    private readonly List<AgentProviderContribution<ISpeechRecognitionProvider>> _speechRecognitionProviders = [];
    private readonly List<AgentProviderContribution<ISpeechSynthesisProvider>> _speechSynthesisProviders = [];

    /// <summary>
    /// Gets the toolset factories declared by the plugin entry point.
    /// </summary>
    public IReadOnlyList<AgentToolsetContribution> Toolsets => _toolsets;

    /// <summary>Gets model provider factories declared by the plugin entry point.</summary>
    public IReadOnlyList<AgentProviderContribution<IModelProvider>> ModelProviders => _modelProviders;

    /// <summary>Gets speech recognition provider factories declared by the plugin entry point.</summary>
    public IReadOnlyList<AgentProviderContribution<ISpeechRecognitionProvider>> SpeechRecognitionProviders => _speechRecognitionProviders;

    /// <summary>Gets speech synthesis provider factories declared by the plugin entry point.</summary>
    public IReadOnlyList<AgentProviderContribution<ISpeechSynthesisProvider>> SpeechSynthesisProviders => _speechSynthesisProviders;

    /// <summary>
    /// Registers a toolset factory that runs once per user scope.
    /// </summary>
    public AgentPluginBuilder AddToolset<TToolset>(Func<AgentPluginContext, TToolset> factory)
        where TToolset : class, ICrewToolset
    {
        ArgumentNullException.ThrowIfNull(factory);
        _toolsets.Add(new AgentToolsetContribution(typeof(TToolset), context => factory(context)));
        return this;
    }

    /// <summary>Registers a model provider factory contributed by this plugin.</summary>
    public AgentPluginBuilder AddModelProvider<TProvider>(Func<AgentPluginContext, TProvider> factory)
        where TProvider : class, IModelProvider
    {
        ArgumentNullException.ThrowIfNull(factory);
        _modelProviders.Add(new AgentProviderContribution<IModelProvider>(typeof(TProvider), context => factory(context)));
        return this;
    }

    /// <summary>Registers a speech recognition provider factory contributed by this plugin.</summary>
    public AgentPluginBuilder AddSpeechRecognitionProvider<TProvider>(Func<AgentPluginContext, TProvider> factory)
        where TProvider : class, ISpeechRecognitionProvider
    {
        ArgumentNullException.ThrowIfNull(factory);
        _speechRecognitionProviders.Add(new AgentProviderContribution<ISpeechRecognitionProvider>(typeof(TProvider), context => factory(context)));
        return this;
    }

    /// <summary>Registers a speech synthesis provider factory contributed by this plugin.</summary>
    public AgentPluginBuilder AddSpeechSynthesisProvider<TProvider>(Func<AgentPluginContext, TProvider> factory)
        where TProvider : class, ISpeechSynthesisProvider
    {
        ArgumentNullException.ThrowIfNull(factory);
        _speechSynthesisProviders.Add(new AgentProviderContribution<ISpeechSynthesisProvider>(typeof(TProvider), context => factory(context)));
        return this;
    }
}
