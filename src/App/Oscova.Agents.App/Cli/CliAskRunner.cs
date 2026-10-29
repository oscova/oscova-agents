using Microsoft.Extensions.Logging;
using Oscova.Agents.Definitions;
using Oscova.Agents.Policies;
using Oscova.Agents.Host.Services.Api;
using Oscova.Agents.Host.Services.Realtime;
using Spectre.Console;

namespace Oscova.Agents.App.Cli;

public sealed class CliAskRunner(ILogger<AGUIChatClient> logger)
{
    /// <summary>
    /// Runs one CLI chat turn through AG-UI and returns the host-owned continuity keys.
    /// </summary>
    public async Task<AskRunResult> AskAsync(HttpClient httpClient, AgentDefinition agent, string prompt, AskRunOptions options, CancellationToken cancellationToken)
    {
        var chatApi = new ChatApiClient(httpClient);
        var client = new AGUIChatClient(httpClient, logger);
        var finalText = string.Empty;
        var messageTexts = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        var writtenLength = 0;
        string? currentMessageId = null;

        var threadId = string.IsNullOrWhiteSpace(options.ThreadId)
            ? Guid.NewGuid().ToString("N")
            : options.ThreadId.Trim();
        var runId = Guid.NewGuid().ToString("N");

        if (options.Jsonl is not null)
        {
            await options.Jsonl.RunStartedAsync(threadId, runId, agent.Id).ConfigureAwait(false);
        }

        CliActivity BeginChatActivity() => new(options.ShowActivity, "Waiting for response...");
        var activity = BeginChatActivity();
        try
        {
            client.OnMessageDeltaReceivedAsync = async payload =>
            {
                if (!string.Equals(currentMessageId, payload.Message.MessageId, StringComparison.Ordinal))
                {
                    currentMessageId = payload.Message.MessageId;
                    writtenLength = 0;
                }

                finalText = payload.Message.Text ?? string.Empty;
                // Preserve message order while repeated snapshots replace their earlier text.
                messageTexts[payload.Message.MessageId] = finalText;
                if (finalText.Length > writtenLength)
                {
                    var delta = finalText[writtenLength..];
                    if (options.Stream)
                    {
                        await activity.StopAsync().ConfigureAwait(false);
                        AnsiConsole.Write(delta);
                    }

                    if (options.Jsonl is not null)
                    {
                        await options.Jsonl.MessageDeltaAsync(delta).ConfigureAwait(false);
                    }

                    writtenLength = finalText.Length;
                }
            };
            client.OnElicitationRequestedAsync = async payload =>
            {
                await activity.StopAsync().ConfigureAwait(false);
                var request = payload.Message.Elicitation;
                if (options.Jsonl is not null && request is not null)
                {
                    await options.Jsonl.ElicitationRequestedAsync(request.RequestId.ToString("N"), request.Message).ConfigureAwait(false);
                }

                await CliPromptResponder.RespondToElicitationAsync(chatApi, payload, options.NoInteractive, cancellationToken).ConfigureAwait(false);

                if (options.Jsonl is not null && request is not null)
                {
                    await options.Jsonl.ElicitationResolvedAsync(request.RequestId.ToString("N"), cancelled: options.NoInteractive, auto: options.NoInteractive).ConfigureAwait(false);
                }

                activity = BeginChatActivity();
            };
            client.OnToolApprovalRequestedAsync = async payload =>
            {
                await activity.StopAsync().ConfigureAwait(false);
                var request = payload.Message.ToolApproval;
                if (options.Jsonl is not null && request is not null)
                {
                    await options.Jsonl.ToolApprovalRequestedAsync(request.RequestId, request.ToolName).ConfigureAwait(false);
                }

                var approved = await CliPromptResponder.RespondToToolApprovalAsync(chatApi, payload, options.NoInteractive, cancellationToken).ConfigureAwait(false);

                if (options.Jsonl is not null && request is not null)
                {
                    await options.Jsonl.ToolApprovalResolvedAsync(request.RequestId, approved, auto: options.NoInteractive).ConfigureAwait(false);
                }

                activity = BeginChatActivity();
            };

            await client.RunAsync(threadId, runId, prompt, agent.Id, options.ModelProviderId, ToolApprovalRunMode.UseAgentPolicy, [], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await activity.StopAsync().ConfigureAwait(false);
        }

        if (options.Stream)
        {
            AnsiConsole.WriteLine();
            RerenderAsMarkdown(finalText);
        }

        finalText = string.Concat(messageTexts.Values);

        if (options.Jsonl is not null)
        {
            await options.Jsonl.RunCompletedAsync(threadId, runId, finalText).ConfigureAwait(false);
        }

        return new AskRunResult(threadId, runId, finalText);
    }

    /// <summary>
    /// Replaces the raw streamed text with its styled markdown form once the reply is complete.
    /// Terminals without cursor control keep the raw stream, which is already readable.
    /// </summary>
    private static void RerenderAsMarkdown(string finalText)
    {
        var console = AnsiConsole.Console;
        // CliOutput.Configure already drops ANSI for --no-color, NO_COLOR, and redirected output,
        // so the console capabilities are the only check needed here.
        var capabilities = console.Profile.Capabilities;
        if (string.IsNullOrWhiteSpace(finalText) || !capabilities.Interactive || !capabilities.Ansi)
        {
            return;
        }

        // Count the rows the raw stream occupied, wrapping included, so the walk back lands on its top line.
        var width = Math.Max(1, console.Profile.Width);
        var rows = 0;
        foreach (var line in finalText.Split('\n'))
        {
            rows += Math.Max(1, (line.TrimEnd('\r').Length + width - 1) / width);
        }

        // Walk back over the streamed block, blank each row, then draw the styled version in its place.
        console.Cursor.Move(CursorDirection.Up, rows);
        for (var row = 0; row < rows; row++)
        {
            console.Cursor.Move(CursorDirection.Left, width);
            console.Write(new string(' ', width - 1));
            console.WriteLine();
        }

        console.Cursor.Move(CursorDirection.Up, rows);
        console.Cursor.Move(CursorDirection.Left, width);
        console.Write(CliMarkdown.Render(finalText));
    }
}

/// <summary>
/// Identifies the host-persisted chat run created by one CLI ask invocation.
/// </summary>
public sealed record AskRunResult(string ThreadId, string RunId, string Answer);

/// <summary>
/// Carries per-run CLI behavior for streaming, interaction, continuity, and machine output.
/// </summary>
public sealed record AskRunOptions(
    bool Stream,
    bool NoInteractive,
    string? ThreadId,
    string? ModelProviderId,
    CliJsonlWriter? Jsonl,
    bool ShowActivity);
