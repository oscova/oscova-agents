using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Infrastructure.Utils;

namespace Oscova.Agents.Infrastructure.Hardware;

/// <summary>Bounds owned worker IO and lifetime, retaining an unconfirmed child instead of accumulating workers.</summary>
public sealed class DiagnosticProcessSupervisor : IDisposable
{
    private readonly ILogger<DiagnosticProcessSupervisor> logger;
    private readonly Func<Process> _createProcess;

    public DiagnosticProcessSupervisor(ILogger<DiagnosticProcessSupervisor> logger) : this(logger, LocalDiagnosticWorker.CreateProcess) { }

    internal DiagnosticProcessSupervisor(ILogger<DiagnosticProcessSupervisor> logger, Func<Process> createProcess)
    {
        this.logger = logger;
        _createProcess = createProcess;
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Process? _unconfirmed;

    internal async Task<DiagnosticProcessResult> RunAsync(DiagnosticRequest request, TimeSpan budget,
        TimeSpan cleanupBudget, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) { return new("skipped", "busy", null, true, 0); }

        var timer = Stopwatch.StartNew();
        Process? process = null;
        var confirmed = true;
        var outcome = "failed";
        var reason = "worker-failed";
        DiagnosticResponse? response = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        using var io = new CancellationTokenSource();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        timeout.CancelAfter(budget);
        try
        {
            if (_unconfirmed is not null)
            {
                if (!_unconfirmed.HasExited) { return new("skipped", "cleanup-unconfirmed", null, false, timer.ElapsedMilliseconds); }

                _unconfirmed.Dispose();
                _unconfirmed = null;
            }

            using var parent = Process.GetCurrentProcess();
            request = request with { ParentId = parent.Id, ParentStartIdentifier = ProcessIdentity.GetStartIdentifier(parent), Deadline = DateTimeOffset.UtcNow + budget };
            var serialized = LocalDiagnosticWorker.Serialize(request);
            if (Encoding.UTF8.GetByteCount(serialized) > LocalDiagnosticWorker.MessageLimit) { throw new InvalidDataException("Diagnostic request exceeds 64 KiB."); }

            process = _createProcess();
            if (!process.Start()) { throw new InvalidOperationException("The diagnostic worker did not start."); }

            confirmed = false;
            stdout = LocalDiagnosticWorker.ReadBoundedAsync(process.StandardOutput, LocalDiagnosticWorker.MessageLimit, io.Token);
            stderr = LocalDiagnosticWorker.ReadBoundedAsync(process.StandardError, 256 * 1024, io.Token, truncate: true);
            await process.StandardInput.WriteAsync(serialized.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            var output = await stdout.WaitAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            confirmed = true;
            var error = await stderr.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                logger.LogWarning("Diagnostic worker {ProcessId} exited with {ExitCode}: {Diagnostic}", process.Id, process.ExitCode, error);
                outcome = ct.IsCancellationRequested || _shutdown.IsCancellationRequested ? "cancelled"
                    : process.ExitCode == 3 ? "timedout" : "failed";
                reason = outcome == "cancelled" ? "request-cancelled" : outcome == "timedout" ? "deadline-exceeded" : "worker-exited";
            }
            else
            {
                if (request.Mode == "passive" && !string.IsNullOrWhiteSpace(error))
                { logger.LogWarning("Passive hardware discovery was incomplete: {Diagnostic}", error); }

                response = LocalDiagnosticWorker.Deserialize(output);
                if ((request.Mode == "passive" && response.Hardware is null) || (request.Mode == "model" && response.Model is null))
                { throw new InvalidDataException("The diagnostic result does not match the requested mode."); }

                outcome = "passed";
                reason = "completed";
            }
        }
        catch (OperationCanceledException)
        {
            outcome = ct.IsCancellationRequested || _shutdown.IsCancellationRequested ? "cancelled" : "timedout";
            reason = outcome == "cancelled" ? "request-cancelled" : "deadline-exceeded";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local diagnostic {Mode} could not complete.", request.Mode);
            reason = ex is InvalidDataException or System.Text.Json.JsonException ? "invalid-result" : "worker-failed";
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(cleanupBudget);
            if (process is not null && !confirmed)
            {
                try
                {
                    if (!process.HasExited) { process.Kill(entireProcessTree: true); }

                    await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
                    confirmed = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not confirm exit of diagnostic worker {ProcessId}. Stop this process manually before retrying.", process.Id);
                    _unconfirmed = process;
                }
            }

            await io.CancelAsync().ConfigureAwait(false);
            if (stdout is not null && stderr is not null)
            {
                try { await Task.WhenAll(stdout, stderr).WaitAsync(cleanup.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException) { }
            }

            if (confirmed) { process?.Dispose(); }

            _gate.Release();
        }

        return new(outcome, reason, response, confirmed, timer.ElapsedMilliseconds);
    }

    /// <summary>Cancels active work and retries termination of a retained child during host shutdown.</summary>
    public void Dispose()
    {
        _shutdown.Cancel();
        if (_unconfirmed is not null)
        {
            try { if (!_unconfirmed.HasExited) { _unconfirmed.Kill(entireProcessTree: true); } }
            catch (Exception ex) { logger.LogError(ex, "Diagnostic worker requires manual shutdown."); }
        }
    }
}

/// <summary>Keeps process outcomes separate from the child provider's inference result.</summary>
internal sealed record DiagnosticProcessResult(string Outcome, string Reason, DiagnosticResponse? Response,
    bool CleanupConfirmed, long ElapsedMilliseconds);
