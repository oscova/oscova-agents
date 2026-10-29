using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Hooks;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Oscova.Agents.Infrastructure.Hooks;

internal sealed class CommandHookExecutor(ILogger<CommandHookExecutor> logger)
{
    /// <summary>
    /// Executes the configured process hook and writes the hook envelope to stdin.
    /// </summary>
    public async Task<HookDispatchResult> ExecuteAsync(
        HookDefinition definition,
        HookEnvelope envelope,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(definition.TimeoutSeconds));
        Process? process = null;

        try
        {
            var command = definition.Command ?? throw new InvalidOperationException("Command hook options are required.");
            var startInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
            {
                startInfo.WorkingDirectory = command.WorkingDirectory;
            }

            foreach (var argument in command.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            AddEnvelopeEnvironment(startInfo, envelope);

            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Hook process did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.StandardInput.WriteAsync(
                JsonSerializer.Serialize(envelope, HookJsonSerializer.Options).AsMemory(),
                timeoutCts.Token).ConfigureAwait(false);
            process.StandardInput.Close();

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            return HookResponseParser.FromCommand(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKillProcessTree(process, logger);
            ct.ThrowIfCancellationRequested();
            return HookDispatchResult.TransportFailure("Hook command timed out.", "timeout");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void AddEnvelopeEnvironment(ProcessStartInfo startInfo, HookEnvelope envelope)
    {
        startInfo.Environment["OSCOVA_HOOK_EVENT_NAME"] = envelope.HookEventName.ToString();
        startInfo.Environment["OSCOVA_HOOK_RUN_ID"] = envelope.HookRunId;
        startInfo.Environment["OSCOVA_HOOK_SCHEMA_VERSION"] = envelope.SchemaVersion.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["OSCOVA_HOOK_TIMESTAMP"] = envelope.Timestamp.ToString("O", CultureInfo.InvariantCulture);
        AddIfPresent(startInfo, "OSCOVA_HOOK_USER_ID", envelope.UserId);
        AddIfPresent(startInfo, "OSCOVA_HOOK_AGENT_ID", envelope.AgentId);
        AddIfPresent(startInfo, "OSCOVA_HOOK_CHAT_THREAD_ID", envelope.ChatThreadId);
        AddIfPresent(startInfo, "OSCOVA_HOOK_TASK_ID", envelope.TaskId);
        AddIfPresent(startInfo, "OSCOVA_HOOK_TASK_RUN_ID", envelope.TaskRunId);
        AddIfPresent(startInfo, "OSCOVA_HOOK_EXECUTION_KIND", envelope.ExecutionKind);
    }

    private static void AddIfPresent(ProcessStartInfo startInfo, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            startInfo.Environment[name] = value;
        }
    }

    private static void TryKillProcessTree(Process? process, ILogger logger)
    {
        if (process?.HasExited != false)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            logger.LogWarning(ex, "Could not terminate cancelled hook process.");
        }
    }
}
