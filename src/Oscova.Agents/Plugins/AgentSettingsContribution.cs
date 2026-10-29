using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Oscova.Agents.Execution;
using Oscova.Agents.Channels;

namespace Oscova.Agents.Plugins;

/// <summary>Identifies the authenticated scope admitted for an interactive settings panel.</summary>
public enum PluginSettingsAccess
{
    /// <summary>Admits the authenticated user.</summary>
    CurrentUser,
    /// <summary>Admits the installation operator.</summary>
    Operator
}

/// <summary>Names Host-controlled settings destinations available to compiled plugin components.</summary>
public static class PluginSettingsSlots
{
    /// <summary>Names the execution provider settings destination.</summary>
    public const string ExecutionEnvironment = "settings.execution.environment";
    /// <summary>Names the selected speech recognition provider's settings destination.</summary>
    public const string SpeechRecognition = "settings.speech.recognition";
    /// <summary>Names the selected speech synthesis provider's settings destination.</summary>
    public const string SpeechSynthesis = "settings.speech.synthesis";
    /// <summary>Names the selected model provider's draft settings destination.</summary>
    public const string ModelProvider = "settings.model-provider";
    /// <summary>Names an independent page in a host-supported settings category.</summary>
    public const string Page = "settings.page";
}

/// <summary>Supplies plugin-owned presentation and placement for an independent settings page.</summary>
public sealed record PluginSettingsPage(string CategoryId, string Title, string? Description = null,
    string? TitleKey = null, string? DescriptionKey = null, string? IconResourceName = null);

/// <summary>Declares a compiled component and its typed context at one owned settings destination.</summary>
public sealed record AgentSettingsContribution(string Id, string SlotId, string? AssociationId,
    Type ComponentType, Func<PluginSettingsContext, object> ContextFactory, PluginSettingsAccess Access,
    string? Heading = null, string? HeadingKey = null, PluginHelpTopic? Help = null)
{
    /// <summary>Gets page metadata for the independent-page destination only.</summary>
    public PluginSettingsPage? Page { get; init; }
    /// <summary>Names supplementary embedded help topics used by this panel's dialogs.</summary>
    public IReadOnlyList<PluginHelpTopic> RelatedHelp { get; init; } = [];
}

/// <summary>Binds local settings keys to one authenticated plugin and storage scope.</summary>
public interface IPluginSettings
{
    /// <summary>Reads an owning plugin's local settings key after current scope admission.</summary>
    Task<string?> ReadAsync(string key, CancellationToken ct = default);
    /// <summary>Writes an owning plugin's local settings key after current authority and vault admission.</summary>
    Task WriteAsync(string key, string content, CancellationToken ct = default);
}

/// <summary>Adapts interactive operations without exposing private component types or a service provider.</summary>
public interface IPluginSettingsUi
{
    /// <summary>Opens the existing text editor using the supplied load and save callbacks.</summary>
    Task<bool> EditTextAsync(string title, Func<Task<string>> load, Func<string, Task> save, string? helpTopic = null);
    /// <summary>Requests confirmation through the existing dialog.</summary>
    Task<bool> ConfirmAsync(string title, string message);
    /// <summary>Shows an existing Host notification.</summary>
    void Notify(string message, PluginNotificationKind kind);
}

/// <summary>Names the semantic notification styles used by settings operations.</summary>
public enum PluginNotificationKind
{
    /// <summary>Reports informational progress.</summary>
    Info,
    /// <summary>Reports completion.</summary>
    Success,
    /// <summary>Reports an expected interruption.</summary>
    Warning,
    /// <summary>Reports a failed action.</summary>
    Error
}

/// <summary>Supplies one admitted panel's authenticated operations and resolved target.</summary>
public sealed record PluginSettingsContext(AgentPluginContext Plugin, string PluginId, string? TargetId, IPluginSettings Settings,
    IPluginSettingsUi Ui, Func<CancellationToken, Task> RequireAccessAsync,
    Func<CancellationToken, Task<bool>> IsOfflineAsync)
{
    /// <summary>Resolves admitted current-user controls for a channel declared by this plugin.</summary>
    public Func<string, CancellationToken, Task<IPluginChannelControl>>? ResolveChannelAsync { get; init; }
    /// <summary>Reads the current speech draft and admitted controls supplied by the host page.</summary>
    public Func<PluginSpeechSettingsContext>? GetSpeechSettings { get; init; }
    /// <summary>Reads the unsaved model draft and field errors supplied by the owning editor.</summary>
    public Func<PluginModelProviderSettingsContext>? GetModelProviderSettings { get; init; }
    /// <summary>Tracks a device operation until disposal and requests its cancellation when authority changes.</summary>
    public Func<Action, IDisposable>? RegisterOperation { get; init; }
}

/// <summary>Declares the device runtime and explicit policies for a plugin execution provider.</summary>
public sealed record AgentExecutionContribution(ExecutionEnvironmentDefinition Definition,
    Func<AgentPluginContext, IExecutionEnvironmentProvider> Factory);

/// <summary>Declares the narrow container process role for transport owners.</summary>
public sealed record AgentContainerLaunchContribution(Func<AgentPluginContext, IContainerProcessLauncher> Factory);
