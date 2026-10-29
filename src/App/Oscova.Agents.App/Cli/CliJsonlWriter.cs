using System.Text.Json;

namespace Oscova.Agents.App.Cli;

/// <summary>
/// Writes the CLI's stable machine event vocabulary as JSON lines for scripted callers.
/// </summary>
public sealed class CliJsonlWriter(TextWriter writer)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Emits the start of a CLI-owned chat run.</summary>
    public Task RunStartedAsync(string threadId, string runId, string agentId) =>
        WriteAsync(new { type = "run.started", threadId, runId, agentId });

    /// <summary>Emits assistant text appended since the previous delta.</summary>
    public Task MessageDeltaAsync(string text) =>
        WriteAsync(new { type = "message.delta", text });

    /// <summary>Emits a tool approval request before the CLI resolves it.</summary>
    public Task ToolApprovalRequestedAsync(string requestId, string toolName) =>
        WriteAsync(new { type = "tool.approval.requested", requestId, toolName });

    /// <summary>Emits the CLI's approval decision for a tool request.</summary>
    public Task ToolApprovalResolvedAsync(string requestId, bool approved, bool auto) =>
        WriteAsync(new { type = "tool.approval.resolved", requestId, approved, auto });

    /// <summary>Emits an elicitation request before the CLI resolves it.</summary>
    public Task ElicitationRequestedAsync(string requestId, string message) =>
        WriteAsync(new { type = "elicitation.requested", requestId, message });

    /// <summary>Emits the CLI's response state for an elicitation request.</summary>
    public Task ElicitationResolvedAsync(string requestId, bool cancelled, bool auto) =>
        WriteAsync(new { type = "elicitation.resolved", requestId, cancelled, auto });

    /// <summary>Emits the completed run and final answer text.</summary>
    public Task RunCompletedAsync(string threadId, string runId, string answer) =>
        WriteAsync(new { type = "run.completed", threadId, runId, answer });

    /// <summary>Emits the terminal error event before the command returns a nonzero exit code.</summary>
    public Task RunErrorAsync(string message) =>
        WriteAsync(new { type = "run.error", message });

    private async Task WriteAsync<T>(T payload)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(payload, JsonOptions)).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }
}
