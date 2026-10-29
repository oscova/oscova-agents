using Oscova.Agents.Conversations;
using System.Net;
using System.Net.Http.Json;
using Oscova.Agents.App.Commands;
using Oscova.Agents.Core.ApiContracts.Agents;
using Oscova.Agents.Definitions;
using Oscova.Agents.Host.Constants;
using Spectre.Console;

namespace Oscova.Agents.App.Cli;

/// <summary>
/// Runs the interactive gateway loop while the host owns all persisted chat state.
/// </summary>
public sealed class GatewaySession(
    CliHostResolver resolver,
    HostApiClient apiClient,
    CliAskRunner askRunner,
    CliSurfaceInspector inspector,
    CliConfigStore configStore,
    LocalHostControl localControl,
    OscovaCommandSettings settings,
    CliSurfaceReport report,
    Func<CliSurfaceReport, CancellationToken, Task<GatewayTarget?>> remoteFallbackAsync)
{
    private static readonly SlashCommand[] SlashCommands =
    [
        new("/help", "list the session commands"),
        new("/status", "show target, auth, and host state"),
        new("/agents", "switch the current agent"),
        new("/model", "override the model provider for this session"),
        new("/thread", "switch to an existing thread"),
        new("/new", "start a fresh thread"),
        new("/clear", "clear the transcript"),
        new("/quit", "leave the gateway")
    ];

    private readonly GatewayComposer _composer = new(SlashCommands);
    private OscovaCommandSettings _settings = settings;
    private CliSurfaceReport _report = report;
    private string _threadId = Guid.NewGuid().ToString("N");
    private string? _threadTitle;
    // The first load has no session selection to override the saved default.
    private AgentDefinition _agent = new() { Id = string.Empty };
    private IReadOnlyList<AgentDefinition> _agents = [];
    private string? _modelProviderId;
    private string _modelName = "user default";

    /// <summary>
    /// Reads commands and chat turns until the user exits the gateway.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await LoadSessionAsync(cancellationToken).ConfigureAwait(false);
                RenderSessionHeader();
                break;
            }
            catch (Exception ex) when (IsConnectionFailure(ex, cancellationToken))
            {
                if (!await TryHandleRemoteConnectionFailureAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }
            }
        }

        while (true)
        {
            var line = await _composer.ReadAsync(BuildFooter(), cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("/", StringComparison.Ordinal))
            {
                if (!await HandleSlashAsync(line, cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                continue;
            }

            await RunTurnAsync(line, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LoadSessionAsync(CancellationToken cancellationToken)
    {
        var host = await resolver.ResolveAsync(_settings, requireToken: true, cancellationToken).ConfigureAwait(false);
        using var client = apiClient.Create(host);
        var configuration = await GetAuthenticatedJsonAsync<AgentConfiguration>(client, host, ApiRoutes.Agents.Configuration, cancellationToken)
.ConfigureAwait(false) ?? new AgentConfiguration();
        var configuredAgentId = configStore.Read().DefaultAgentId;
        _agents = [configuration.MainAgent, .. configuration.CustomAgents];
        _agent = ResolveAgent(configuration, _agent.Id, configuredAgentId);
        _report = await inspector.InspectAsync(_settings, cancellationToken).ConfigureAwait(false);
    }

    private void RenderSessionHeader()
    {
        CliRender.KeyValue("Gateway",
        [
            ("Host", CliRender.Value(_report.TargetHostUrl)),
            ("User", CliRender.Value(_report.Bearer.UserName ?? "not logged in")),
            ("Agent", CliRender.Value(_agent.Name)),
            ("Model", CliRender.Value(_modelName)),
            ("Thread", CliRender.Identifier(ThreadLabel())),
            ("Hint", CliRender.Value("/help for commands"))
        ]);
    }

    private GatewayFooterState BuildFooter() =>
        new(_report.TargetHostUrl, _agent.Name, _modelName, ThreadLabel());

    // Titles come from the thread picker only; a thread started here has none until the host names it.
    private string ThreadLabel() => string.IsNullOrWhiteSpace(_threadTitle) ? ShortId(_threadId) : _threadTitle;

    private async Task<bool> HandleSlashAsync(string line, CancellationToken cancellationToken)
    {
        var command = line.Trim();
        try
        {
            if (command.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (command.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                RenderHelp();
                return true;
            }

            if (command.Equals("/status", StringComparison.OrdinalIgnoreCase))
            {
                await StatusCommand.RenderCurrentStatusAsync(resolver, apiClient, localControl, inspector, _settings, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (command.Equals("/agents", StringComparison.OrdinalIgnoreCase))
            {
                await SelectAgentAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (command.Equals("/model", StringComparison.OrdinalIgnoreCase))
            {
                await SelectModelAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (command.Equals("/thread", StringComparison.OrdinalIgnoreCase))
            {
                await SelectThreadAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (command.Equals("/new", StringComparison.OrdinalIgnoreCase))
            {
                _threadId = Guid.NewGuid().ToString("N");
                _threadTitle = null;
                CliRender.KeyValue("New conversation",
                [
                    ("Thread", CliRender.Identifier(ShortId(_threadId)))
                ]);
                return true;
            }

            if (command.Equals("/clear", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.Clear();
                RenderSessionHeader();
                return true;
            }

            AnsiConsole.Write(CliRender.Card(
                "Unknown command",
                new Text("Use /help.", CliTheme.Value),
                CliTheme.Warning));
            return true;
        }
        catch (CliExitException ex)
        {
            CliOutput.Error(ex.Message);
            return true;
        }
        catch (HttpRequestException ex)
        {
            CliOutput.Error(ex.Message);
            return true;
        }
    }

    /// <summary>
    /// Lists the slash commands handled inside a gateway session. These are not the shell commands
    /// registered in Program.cs; the two help trees are separate and only overlap by this pointer.
    /// </summary>
    private static void RenderHelp()
    {
        var table = CliRender.Table("Command", "Action");

        foreach (var command in SlashCommands)
        {
            table.AddRow(new Text(command.Name, CliTheme.Label), new Text(command.Description, CliTheme.Value));
        }

        table.AddRow(new Text("up arrow", CliTheme.Label), new Text("recall an earlier prompt", CliTheme.Value));
        table.AddRow(new Text("tab", CliTheme.Label), new Text("complete the highlighted slash command", CliTheme.Value));
        AnsiConsole.Write(table);

        // These are session commands only; the shell command surface is a separate help tree.
        AnsiConsole.MarkupLine("[grey58]Run [grey70]oscova --help[/] outside the gateway for the full command list.[/]");
    }

    private async Task SelectAgentAsync(CancellationToken cancellationToken)
    {
        await LoadSessionAsync(cancellationToken).ConfigureAwait(false);
        if (_agents.Count == 0)
        {
            AnsiConsole.Write(CliRender.Card(
                "Agents",
                new Text("No agents are available.", CliTheme.Value),
                CliTheme.Warning));
            return;
        }

        // The prompt carries the agent itself, so no identifier is parsed back out of the rendered label.
        _agent = await PickAsync(
            "Choose an agent",
            _agents,
            agent => $"{agent.Name.EscapeMarkup()} [{CliTheme.LabelMarkup}]{agent.Id.EscapeMarkup()}[/]",
            cancellationToken).ConfigureAwait(false);
        CliRender.KeyValue("Agent selected",
        [
            ("Name", CliRender.Value(_agent.Name)),
            ("ID", CliRender.Identifier(_agent.Id))
        ]);
    }

    private async Task SelectModelAsync(CancellationToken cancellationToken)
    {
        var host = await resolver.ResolveAsync(_settings, requireToken: true, cancellationToken).ConfigureAwait(false);
        using var client = apiClient.Create(host);
        var configuration = await GetAuthenticatedJsonAsync<AgentConfiguration>(client, host, ApiRoutes.Agents.Configuration, cancellationToken)
            .ConfigureAwait(false) ?? new AgentConfiguration();

        var choices = new List<ModelChoice> { new(null, "user default") };
        // Chat choices share the host's catalog, which excludes embedding-only providers.
        foreach (var provider in configuration.AvailableModelProviders)
        {
            choices.Add(new ModelChoice(provider.Id, provider.Name));
        }

        var selected = await PickAsync(
            "Choose a model provider",
            choices,
            choice => choice.Id is null
                ? choice.Name.EscapeMarkup()
                : $"{choice.Name.EscapeMarkup()} [{CliTheme.LabelMarkup}]{choice.Id.EscapeMarkup()}[/]",
            cancellationToken).ConfigureAwait(false);

        _modelProviderId = selected.Id;
        _modelName = selected.Name;
        if (selected.Id is null)
        {
            CliRender.KeyValue("Model selected",
            [
                ("Provider", CliRender.Value("user default"))
            ]);
        }
        else
        {
            CliRender.KeyValue("Model selected",
            [
                ("Provider", CliRender.Value(selected.Name)),
                ("ID", CliRender.Identifier(selected.Id))
            ]);
        }
    }

    private async Task SelectThreadAsync(CancellationToken cancellationToken)
    {
        var host = await resolver.ResolveAsync(_settings, requireToken: true, cancellationToken).ConfigureAwait(false);
        using var client = apiClient.Create(host);
        var threads = await GetAuthenticatedJsonAsync<List<ChatThread>>(client, host, ApiRoutes.Chat.Threads, cancellationToken).ConfigureAwait(false) ?? [];
        if (threads.Count == 0)
        {
            CliRender.KeyValue("Threads",
            [
                ("Current", CliRender.Identifier(ShortId(_threadId))),
                ("Available", CliRender.Value("none"))
            ]);
            return;
        }

        var selected = await PickAsync(
            "Choose a thread",
            threads,
            thread => $"{DisplayTitle(thread).EscapeMarkup()} [{CliTheme.LabelMarkup}]{ShortId(thread.Id).EscapeMarkup()}[/]",
            cancellationToken).ConfigureAwait(false);

        _threadId = selected.Id;
        var title = DisplayTitle(selected);
        _threadTitle = title;
        CliRender.KeyValue("Thread selected",
        [
            ("Title", CliRender.Value(title)),
            ("ID", CliRender.Identifier(ShortId(_threadId)))
        ]);
    }

    private static async Task<T> PickAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> converter, CancellationToken cancellationToken)
        where T : notnull
    {
        // Search only earns its line of chrome once the list stops fitting comfortably on screen.
        var prompt = new SelectionPrompt<T> { SearchEnabled = choices.Count > 8 };
        prompt.Title($"[{CliTheme.HeadingMarkup}]{title.EscapeMarkup()}[/]")
            .PageSize(15)
            .UseConverter(converter)
            .AddChoices(choices);

        return await AnsiConsole.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunTurnAsync(string prompt, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ConsoleCancelEventHandler? cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                runCancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            ResolvedHost? host = null;

            try
            {
                host = await resolver.ResolveAsync(_settings, requireToken: true, runCancellation.Token).ConfigureAwait(false);
                using var client = apiClient.Create(host);
                var capabilities = AnsiConsole.Profile.Capabilities;
                var result = await askRunner.AskAsync(
                    client,
                    _agent,
                    prompt,
                    new AskRunOptions(
                        Stream: true,
                        NoInteractive: false,
                        ThreadId: _threadId,
                        ModelProviderId: _modelProviderId,
                        Jsonl: null,
                        ShowActivity: !_settings.Json && !_settings.Quiet && !_settings.NoInteractive && capabilities.Interactive && capabilities.Ansi),
                    runCancellation.Token).ConfigureAwait(false);
                _threadId = result.ThreadId;
                return;
            }
            catch (HttpRequestException ex) when (host is not null && IsAuthenticationFailure(ex))
            {
                throw CreateAuthenticationExit(host, ex.StatusCode);
            }
            // Ctrl+C stops the CLI wait while an admitted Host run continues independently.
            catch (OperationCanceledException) when (runCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("[grey58]run canceled[/]");
                return;
            }
            // Recovery resubmits the prompt because transport failure leaves delivery unknown.
            catch (Exception ex) when (IsConnectionFailure(ex, cancellationToken))
            {
                if (!await TryHandleRemoteConnectionFailureAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }
    }

    private async Task<bool> TryHandleRemoteConnectionFailureAsync(CancellationToken cancellationToken)
    {
        if (_report.TargetIsLoopback)
        {
            return false;
        }

        var fallback = await remoteFallbackAsync(_report, cancellationToken).ConfigureAwait(false);
        if (fallback is null)
        {
            return true;
        }

        _settings = fallback.Settings;
        _report = fallback.Report;
        await LoadSessionAsync(cancellationToken).ConfigureAwait(false);
        RenderSessionHeader();
        return true;
    }

    private static AgentDefinition ResolveAgent(AgentConfiguration configuration, string? currentAgentId, string? configuredAgentId)
    {
        var requested = string.IsNullOrWhiteSpace(currentAgentId) ? configuredAgentId : currentAgentId;
        if (string.IsNullOrWhiteSpace(requested) ||
            string.Equals(requested, configuration.MainAgent.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requested, "main", StringComparison.OrdinalIgnoreCase))
        {
            return configuration.MainAgent;
        }

        return configuration.CustomAgents.FirstOrDefault(agent =>
            string.Equals(agent.Id, requested, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(agent.Name, requested, StringComparison.OrdinalIgnoreCase))
            ?? configuration.MainAgent;
    }

    private static bool IsConnectionFailure(Exception exception, CancellationToken cancellationToken)
    {
        return exception is HttpRequestException { StatusCode: null } ||
            exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;
    }

    private static async Task<T?> GetAuthenticatedJsonAsync<T>(
        HttpClient client,
        ResolvedHost host,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetFromJsonAsync<T>(path, CliJson.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsAuthenticationFailure(ex))
        {
            throw CreateAuthenticationExit(host, ex.StatusCode);
        }
    }

    private static bool IsAuthenticationFailure(HttpRequestException exception)
    {
        return exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    }

    private static CliExitException CreateAuthenticationExit(ResolvedHost host, HttpStatusCode? statusCode)
    {
        return statusCode == HttpStatusCode.Forbidden
            ? new CliExitException(3, $"This CLI login is not authorized for {host.HostUrl}. Run 'oscova login --host {host.HostUrl}' with an authorized account.")
            : new CliExitException(2, $"Your CLI login for {host.HostUrl} is no longer valid. Run 'oscova login --host {host.HostUrl}' again.");
    }

    private static string DisplayTitle(ChatThread thread)
    {
        if (!string.IsNullOrWhiteSpace(thread.Title))
        {
            return thread.Title;
        }

        return string.IsNullOrWhiteSpace(thread.AgentName) ? "(untitled)" : thread.AgentName;
    }

    private static string ShortId(string id) => id.Length <= 12 ? id : id[..12];
}

/// <summary>
/// One entry in the session model picker; a null identifier means the user default stays in force.
/// </summary>
public sealed record ModelChoice(string? Id, string Name);

/// <summary>
/// Carries the active target settings and surface report after gateway startup or fallback.
/// </summary>
public sealed record GatewayTarget(OscovaCommandSettings Settings, CliSurfaceReport Report);
