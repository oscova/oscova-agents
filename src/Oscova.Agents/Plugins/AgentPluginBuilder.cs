using System;
using System.Collections.Generic;
using System.Linq;
using Oscova.Agents.Llm;
using Oscova.Agents.Speech;
using Oscova.Agents.Channels;
using Oscova.Agents.Credentials;
using Oscova.Agents.Tasks;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Plugins;

/// <summary>
/// Collects contributions declared by a plugin entry point.
/// </summary>
public sealed class AgentPluginBuilder
{
    private readonly List<AgentExecutionContribution> _executionProviders = [];
    private readonly List<AgentContainerLaunchContribution> _containerLaunchers = [];
    private readonly List<AgentSettingsContribution> _settingsPanels = [];

    /// <summary>Gets execution provider declarations from this plugin.</summary>
    public IReadOnlyList<AgentExecutionContribution> ExecutionProviders => _executionProviders;
    /// <summary>Gets container process launch declarations from this plugin.</summary>
    public IReadOnlyList<AgentContainerLaunchContribution> ContainerLaunchers => _containerLaunchers;
    /// <summary>Gets compiled settings component declarations from this plugin.</summary>
    public IReadOnlyList<AgentSettingsContribution> SettingsPanels => _settingsPanels;

    /// <summary>Registers an execution runtime with explicit supported policies.</summary>
    public AgentPluginBuilder AddExecutionProvider(AgentExecutionContribution contribution)
        => Register("Execution provider", contribution.Definition.Id, () =>
        {
            if (string.IsNullOrWhiteSpace(contribution.Definition.Id) ||
                string.Equals(contribution.Definition.Id, "host", StringComparison.OrdinalIgnoreCase))
            { throw new ArgumentException("Execution provider Id is required and 'host' is reserved."); }
            _executionProviders.Add(contribution);
        });

    /// <summary>Registers the narrow container process launch role.</summary>
    public AgentPluginBuilder AddContainerLauncher(AgentContainerLaunchContribution contribution)
        => Register("Container launcher", null, () => _containerLaunchers.Add(contribution));

    /// <summary>Registers a compiled settings component without an ASP.NET dependency.</summary>
    public AgentPluginBuilder AddSettingsPanel(AgentSettingsContribution contribution)
        => Register("Settings panel", contribution.Id, () =>
        {
            if (string.IsNullOrWhiteSpace(contribution.Id) ||
                (contribution.SlotId != PluginSettingsSlots.Page && string.IsNullOrWhiteSpace(contribution.AssociationId)) ||
                (contribution.SlotId == PluginSettingsSlots.Page && contribution.AssociationId is not null))
            { throw new ArgumentException("Settings panel Id is required. Inline panels require AssociationId and independent pages must omit it."); }
            _settingsPanels.Add(contribution);
        });

    private readonly List<AgentChannelContribution> _channels = [];
    private readonly List<CredentialDeclaration> _credentials = [];
    private readonly List<OAuthProviderDefinition> _oauthProviders = [];
    private readonly List<AgentTemplateDefinition> _agentTemplates = [];
    private readonly List<AgentTaskContribution> _tasks = [];
    private readonly List<AgentInstructionContribution> _instructions = [];
    private readonly List<string> _diagnostics = [];
    private static readonly HashSet<string> ProtocolParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "client_id", "client_secret", "redirect_uri", "response_type", "scope", "state"
    };

    /// <summary>Gets rejected contribution declarations for the loading host.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>Gets the resource marker used to resolve this plugin's display strings.</summary>
    public Type? ResourceType { get; private set; }

    /// <summary>Registers the marker for this plugin's standard Resources resource family.</summary>
    public AgentPluginBuilder UseResources<TResource>()
    {
        ResourceType = typeof(TResource);
        return this;
    }

    /// <summary>Gets task declarations registered by this plugin.</summary>
    public IReadOnlyList<AgentTaskContribution> Tasks => _tasks;

    /// <summary>Gets default instruction declarations registered by this plugin.</summary>
    public IReadOnlyList<AgentInstructionContribution> Instructions => _instructions;

    /// <summary>Registers default guidance under a unique lowercase plugin-local key.</summary>
    public AgentPluginBuilder AddInstruction(string key, string label, string defaultInstruction)
        => Register("Instruction", key, () =>
        {
            if (string.IsNullOrWhiteSpace(key) ||
                key.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            {
                throw new ArgumentException("Instruction key must contain lowercase ASCII letters, digits or hyphens.", nameof(key));
            }
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(defaultInstruction))
            {
                throw new ArgumentException("Instruction label and default text are required.");
            }
            if (_instructions.Any(instruction => instruction.Key == key))
            {
                throw new ArgumentException($"Instruction key '{key}' is already registered by this plugin.", nameof(key));
            }
            _instructions.Add(new(key, label, defaultInstruction));
        });

    /// <summary>Registers task defaults without an execution handler.</summary>
    public AgentPluginBuilder AddTask(string key, Func<AgentPluginContext, AgentTaskDefinition> definitionFactory)
        => AddTaskCore(new(key, definitionFactory));

    /// <summary>Registers task defaults and a deterministic handler created for each run.</summary>
    public AgentPluginBuilder AddTask(string key, Func<AgentPluginContext, AgentTaskDefinition> definitionFactory,
        Func<AgentPluginContext, IDeterministicTaskHandler> deterministicHandlerFactory)
        => AddTaskCore(new(key, definitionFactory, DeterministicHandlerFactory: deterministicHandlerFactory));

    /// <summary>Registers deterministic task defaults with the configured provider entries required before execution.</summary>
    public AgentPluginBuilder AddTask(string key, Func<AgentPluginContext, AgentTaskDefinition> definitionFactory,
        Func<AgentPluginContext, IDeterministicTaskHandler> deterministicHandlerFactory,
        Func<AgentPluginContext, AgentTaskDefinition, IReadOnlyList<Guid>> requiredProviders)
        => AddTaskCore(new(key, definitionFactory, DeterministicHandlerFactory: deterministicHandlerFactory,
            RequiredProviders: requiredProviders));

    /// <summary>Registers task defaults and an agent preparation handler created for each run.</summary>
    public AgentPluginBuilder AddTask(string key, Func<AgentPluginContext, AgentTaskDefinition> definitionFactory,
        Func<AgentPluginContext, IAgentTaskHandler> agentHandlerFactory)
        => AddTaskCore(new(key, definitionFactory, AgentHandlerFactory: agentHandlerFactory));

    private AgentPluginBuilder AddTaskCore(AgentTaskContribution contribution)
        => Register("Task", contribution.Key, () =>
        {
            var key = contribution.Key;
            if (string.IsNullOrWhiteSpace(key) ||
                key.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            {
                throw new ArgumentException("Task key must contain lowercase ASCII letters, digits or hyphens.", nameof(contribution));
            }
            if (_tasks.Any(task => task.Key == key))
            {
                throw new ArgumentException($"Task key '{key}' is already registered by this plugin.", nameof(contribution));
            }
            ArgumentNullException.ThrowIfNull(contribution.DefinitionFactory);
            _tasks.Add(contribution);
        });

    /// <summary>Gets the agent starters declared by this plugin.</summary>
    public IReadOnlyList<AgentTemplateDefinition> AgentTemplates => _agentTemplates;

    /// <summary>Registers descriptive template content with a unique plugin-local identifier.</summary>
    public AgentPluginBuilder AddAgentTemplate(AgentTemplateDefinition definition)
        => Register("Agent template", definition?.Id, () =>
        {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Id) ||
            definition.Id.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            throw new ArgumentException("Template Id must contain lowercase ASCII letters, digits or hyphens.", nameof(definition));
        }
        if (string.IsNullOrWhiteSpace(definition.Name) ||
            string.IsNullOrWhiteSpace(definition.Description) ||
            string.IsNullOrWhiteSpace(definition.SystemInstruction))
        {
            throw new ArgumentException("Template Name, Description and SystemInstruction are required.", nameof(definition));
        }
        if (_agentTemplates.Any(template => template.Id == definition.Id))
        {
            throw new ArgumentException($"Template Id '{definition.Id}' is already registered by this plugin.", nameof(definition));
        }

        _agentTemplates.Add(definition);
        });

    /// <summary>Gets the account providers registered by this plugin.</summary>
    public IReadOnlyList<OAuthProviderDefinition> OAuthProviders => _oauthProviders;

    /// <summary>Registers an OAuth provider and its system app credential declaration.</summary>
    public AgentPluginBuilder AddOAuthProvider(OAuthProviderDefinition definition)
        => Register("OAuth provider", definition?.Id, () =>
        {
            ArgumentNullException.ThrowIfNull(definition);
            ValidateCredential(definition.AppCredential);
            if (!IsValidOAuthProvider(definition) || _oauthProviders.Any(item => item.Id == definition.Id) ||
                _credentials.Any(item => string.Equals(item.Key, definition.AppCredential.Key, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("The OAuth declaration is invalid or its provider or credential key is already registered.");
            }
            _oauthProviders.Add(definition);
            _credentials.Add(definition.AppCredential);
        });

    /// <summary>Gets channel declarations registered by this plugin.</summary>
    public IReadOnlyList<AgentChannelContribution> Channels => _channels;

    /// <summary>Gets structured credential declarations registered by this plugin.</summary>
    public IReadOnlyList<CredentialDeclaration> Credentials => _credentials;

    /// <summary>Registers a channel without starting its connection.</summary>
    public AgentPluginBuilder AddChannel(IAgentChannel channel)
        => Register("Channel", channel?.Definition.Id, () =>
        {
            ArgumentNullException.ThrowIfNull(channel);
            var definition = channel.Definition;
            if (!PathHelper.IsSafePathSegment(definition.Id) ||
                string.IsNullOrWhiteSpace(definition.Title) ||
                _channels.Any(item => string.Equals(item.Definition.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("Channel declarations require unique safe identifiers and a title.");
            }
            var topics = new List<PluginHelpTopic>(definition.RelatedHelp);
            if (definition.Help is { } primary) { topics.Add(primary); }
            var existingTopics = _channels.SelectMany(item => item.Definition.RelatedHelp.Concat(
                item.Definition.Help is { } help ? new[] { help } : Array.Empty<PluginHelpTopic>()));
            var ids = existingTopics.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var topic in topics)
            {
                if (string.IsNullOrWhiteSpace(topic.Id) ||
                    topic.Id.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) ||
                    string.IsNullOrWhiteSpace(topic.ResourceName) || !ids.Add(topic.Id) ||
                    topic.Translations.Any(item => string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)))
                {
                    throw new ArgumentException("Help topics require unique lowercase identifiers and nonblank embedded resource names.");
                }
            }
            _channels.Add(new AgentChannelContribution(channel));
        });

    /// <summary>Registers a structured credential slot used by this plugin.</summary>
    public AgentPluginBuilder AddCredential(CredentialDeclaration declaration)
        => Register("Credential", declaration?.Key, () =>
        {
            ValidateCredential(declaration!);
            if (_credentials.Any(item => string.Equals(item.Key, declaration!.Key, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("The credential key is already registered.");
            }
            _credentials.Add(declaration!);
        });

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
    public AgentPluginBuilder AddToolset<TToolset>(Func<AgentPluginContext, TToolset> factory,
        bool supportsNodeExecution = false)
        where TToolset : class, IAgentToolset
        => Register("Toolset", typeof(TToolset).FullName, () =>
        {
            ArgumentNullException.ThrowIfNull(factory);
            _toolsets.Add(new AgentToolsetContribution(typeof(TToolset), context => factory(context), supportsNodeExecution));
        });

    /// <summary>Registers a model provider factory contributed by this plugin.</summary>
    public AgentPluginBuilder AddModelProvider<TProvider>(Func<AgentPluginContext, TProvider> factory)
        where TProvider : class, IModelProvider
        => Register("Provider", typeof(TProvider).FullName, () =>
        {
            ArgumentNullException.ThrowIfNull(factory);
            _modelProviders.Add(new AgentProviderContribution<IModelProvider>(typeof(TProvider), context => factory(context)));
        });

    /// <summary>Registers a speech recognition provider factory contributed by this plugin.</summary>
    public AgentPluginBuilder AddSpeechRecognitionProvider<TProvider>(Func<AgentPluginContext, TProvider> factory)
        where TProvider : class, ISpeechRecognitionProvider
        => Register("Provider", typeof(TProvider).FullName, () =>
        {
            ArgumentNullException.ThrowIfNull(factory);
            _speechRecognitionProviders.Add(new AgentProviderContribution<ISpeechRecognitionProvider>(typeof(TProvider), context => factory(context)));
        });

    /// <summary>Registers a speech synthesis provider factory contributed by this plugin.</summary>
    public AgentPluginBuilder AddSpeechSynthesisProvider<TProvider>(Func<AgentPluginContext, TProvider> factory)
        where TProvider : class, ISpeechSynthesisProvider
        => Register("Provider", typeof(TProvider).FullName, () =>
        {
            ArgumentNullException.ThrowIfNull(factory);
            _speechSynthesisProviders.Add(new AgentProviderContribution<ISpeechSynthesisProvider>(typeof(TProvider), context => factory(context)));
        });

    private AgentPluginBuilder Register(string kind, string? key, Action register)
    {
        try { register(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _diagnostics.Add($"{kind} '{key ?? "<null>"}' was rejected: {ex.Message}");
        }
        return this;
    }

    private static void ValidateCredential(CredentialDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (string.IsNullOrWhiteSpace(declaration.Key) || string.IsNullOrWhiteSpace(declaration.Label) ||
            declaration.Fields.Count == 0 || declaration.Fields.Any(field =>
                string.IsNullOrWhiteSpace(field.Name) || string.IsNullOrWhiteSpace(field.Label)) ||
            declaration.Fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != declaration.Fields.Count)
        {
            throw new ArgumentException("Credential declarations require a key, label, and distinct named, labelled fields.");
        }
    }

    private static bool IsValidOAuthProvider(OAuthProviderDefinition definition)
    {
        var credential = definition.AppCredential;
        return !string.IsNullOrWhiteSpace(definition.Id) &&
            definition.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
            definition.Id == definition.Id.ToLowerInvariant() &&
            !string.IsNullOrWhiteSpace(definition.DisplayName) &&
            IsEndpoint(definition.AuthorizationEndpoint) && IsEndpoint(definition.TokenEndpoint) && IsEndpoint(definition.UserInfoEndpoint) &&
            definition.Scopes.Count > 0 && definition.Scopes.All(scope => !string.IsNullOrWhiteSpace(scope)) &&
            !definition.AuthorizationParameters.Keys.Any(ProtocolParameters.Contains) &&
            credential.Key == $"oauth-provider:{definition.Id}" && !string.IsNullOrWhiteSpace(credential.Label) &&
            credential.Scope == CredentialScope.System && credential.Fields.Count == 2 &&
            credential.Fields.Count(field => field.Name == OAuthProviderDefinition.ClientIdField && !field.IsSecret && field.IsRequired) == 1 &&
            credential.Fields.Count(field => field.Name == OAuthProviderDefinition.ClientSecretField && field.IsSecret && field.IsRequired) == 1;
    }

    private static bool IsEndpoint(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
}
