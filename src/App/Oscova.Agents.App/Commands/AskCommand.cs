using System.ComponentModel;
using System.Net.Http.Json;
using Oscova.Agents.App.Cli;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.ApiContracts.Agents;
using Oscova.Agents.Definitions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Commands;

public sealed class AskCommand(
    CliHostResolver resolver,
    HostApiClient apiClient,
    CliConfigStore configStore,
    CliAskRunner askRunner) : AsyncCommand<AskCommand.Settings>
{
    public sealed class Settings : OscovaCommandSettings
    {
        [CommandArgument(0, "[PROMPT]")]
        [Description("The question to ask. Omit it to read the prompt from standard input.")]
        public string? Prompt { get; init; }

        [CommandOption("-a|--agent <AGENT_ID>")]
        [Description("Agent to answer the question. List valid ids with 'oscova agents list'.")]
        public string? AgentId { get; init; }

        [CommandOption("-t|--thread <THREAD_ID>")]
        [Description("Continue a specific thread. List valid ids with 'oscova threads list'.")]
        public string? ThreadId { get; init; }

        [CommandOption("-c|--continue")]
        [Description("Continue the most recently updated thread.")]
        public bool Continue { get; init; }

        [CommandOption("--model <MODEL_PROVIDER_ID>")]
        [Description("Model provider to answer with. List valid ids with 'oscova models list'.")]
        public string? ModelProviderId { get; init; }

        [CommandOption("--jsonl")]
        [Description("Stream stable machine events as JSON lines.")]
        public bool Jsonl { get; init; }

        [CommandOption("-s|--stream")]
        [Description("Print the answer token by token as it arrives.")]
        public bool Stream { get; init; }

        [CommandOption("--no-stream")]
        [Description("Wait for the complete answer and print it once. Overrides --stream.")]
        public bool NoStream { get; init; }

        [CommandOption("--timeout <SECONDS>")]
        [Description("Maximum wait time for the answer.")]
        public int? TimeoutSeconds { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        CliOutput.Configure(settings);
        var jsonl = settings.Jsonl ? new CliJsonlWriter(Console.Out) : null;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler? cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            if (settings.TimeoutSeconds is > 0)
            {
                cancellation.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds.Value));
            }

            return await ExecuteCoreAsync(settings, jsonl, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (jsonl is not null)
        {
            await jsonl.RunErrorAsync(ex.Message).ConfigureAwait(false);
            throw;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            cancellation.Dispose();
        }
    }

    private async Task<int> ExecuteCoreAsync(Settings settings, CliJsonlWriter? jsonl, CancellationToken cancellationToken)
    {
        if (settings.Stream && settings.NoStream)
        {
            throw new CliExitException(4, "Use either --stream or --no-stream, not both.");
        }

        if (settings.Json && settings.Jsonl)
        {
            throw new CliExitException(4, "Use either --json or --jsonl, not both.");
        }

        if (!string.IsNullOrWhiteSpace(settings.ThreadId) && settings.Continue)
        {
            throw new CliExitException(4, "Use either --thread or --continue, not both.");
        }

        var machineMode = settings.Json || settings.Jsonl;
        var noInteractive = settings.NoInteractive || machineMode;
        var capabilities = AnsiConsole.Profile.Capabilities;
        var host = await resolver.ResolveAsync(settings, requireToken: true, cancellationToken).ConfigureAwait(false);
        using var client = apiClient.Create(host);
        var config = configStore.Read();
        var configuration = await client.GetFromJsonAsync<AgentConfiguration>("api/agents", CliJson.Options, cancellationToken).ConfigureAwait(false) ?? new AgentConfiguration();
        var agent = ResolveAgent(configuration, settings.AgentId ?? config.DefaultAgentId);

        var prompt = await ResolvePromptAsync(settings, noInteractive, cancellationToken).ConfigureAwait(false);
        var threadId = settings.Continue ? await ResolveContinueThreadAsync(client, cancellationToken).ConfigureAwait(false) : settings.ThreadId;
        var stream = !machineMode && ResolveStreamPreference(settings, config.DefaultStream);
        var result = await askRunner.AskAsync(
            client,
            agent,
            prompt,
            new AskRunOptions(
                stream,
                noInteractive,
                threadId,
                settings.ModelProviderId,
                jsonl,
                !machineMode && !settings.Quiet && !noInteractive && capabilities.Interactive && capabilities.Ansi),
            cancellationToken).ConfigureAwait(false);

        if (settings.Json)
        {
            await CliOutput.WriteJsonAsync(new
            {
                agentId = agent.Id,
                threadId = result.ThreadId,
                runId = result.RunId,
                answer = result.Answer
            }, cancellationToken).ConfigureAwait(false);
        }
        else if (!settings.Jsonl)
        {
            if (!stream)
            {
                AnsiConsole.WriteLine(result.Answer);
            }

            if (!settings.Quiet)
            {
                AnsiConsole.MarkupLine($"[grey58]thread {result.ThreadId.EscapeMarkup()}[/]");
            }
        }

        return 0;
    }

    private static bool ResolveStreamPreference(Settings settings, bool? configuredDefault)
    {
        if (settings.Stream)
        {
            return true;
        }

        if (settings.NoStream)
        {
            return false;
        }

        return configuredDefault ?? false;
    }

    private static AgentDefinition ResolveAgent(AgentConfiguration configuration, string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId) ||
            string.Equals(agentId, configuration.MainAgent.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(agentId, "main", StringComparison.OrdinalIgnoreCase))
        {
            return configuration.MainAgent;
        }

        return configuration.CustomAgents.FirstOrDefault(agent =>
            string.Equals(agent.Id, agentId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(agent.Name, agentId, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliExitException(2, $"Agent '{agentId}' was not found.");
    }

    private static async Task<string> ResolvePromptAsync(Settings settings, bool noInteractive, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.Prompt))
        {
            return settings.Prompt;
        }

        if (Console.IsInputRedirected)
        {
            using var reader = new StreamReader(Console.OpenStandardInput());
            var stdin = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stdin))
            {
                return stdin.Trim();
            }
        }

        if (noInteractive)
        {
            throw new CliExitException(4, "Provide a prompt argument or pipe text into stdin.");
        }

        return AnsiConsole.Prompt(new TextPrompt<string>("Prompt"));
    }

    private static async Task<string> ResolveContinueThreadAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var threads = await client.GetFromJsonAsync<List<ChatThread>>("api/chat/threads", CliJson.Options, cancellationToken).ConfigureAwait(false);
        return threads?.FirstOrDefault()?.Id
            ?? throw new CliExitException(1, "No previous thread to continue.");
    }
}
