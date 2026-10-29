using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Execution;
using Oscova.Agents.Harness;
using Oscova.Agents.Providers;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Infrastructure.Skills;

/// <summary>Retains public job identity, user ownership, bounded output and permission cancellation across execution backends.</summary>
public sealed class BackgroundShellJobRegistry(ShellExecutorFactory shellExecutorFactory,
    RunContextAccessor runContextAccessor, ILogger<BackgroundShellJobRegistry> logger,
    ExecutionPermissionRegistry executions) : IShellBackgroundJobProvider, IAsyncDisposable
{
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    public async Task<StartJobResult> StartAsync(string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command)) { return new(false, null, "Command is required."); }

        var run = runContextAccessor.Current ?? throw new InvalidOperationException("Background jobs require an active run.");
        if (!ShellCommandPolicy.For(run).Evaluate(new ShellRequest(command)).Allowed)
        { throw new ToolFailureException("Shell command was rejected by the execution policy."); }

        Job? job = null;
        var transferred = false;
        ExecutionPermissionRegistry.ConfigurationScope? configuration = null;
        var permission = executions.Register(run.UserId, UserPermissionKeys.ForShell(run.ProjectId is not null, run.AllowedDirectories),
            CancellationToken.None, async () =>
            {
                await _startGate.WaitAsync().ConfigureAwait(false);
                try { await RemoveAsync(candidate => ReferenceEquals(candidate, job), CancellationToken.None).ConfigureAwait(false); }
                finally { _startGate.Release(); }
            });
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, permission.Token);
        try
        {
            configuration = shellExecutorFactory.TrackBackgroundConfiguration(run, async () =>
            {
                await _startGate.WaitAsync().ConfigureAwait(false);
                try { await RemoveAsync(candidate => ReferenceEquals(candidate, job), CancellationToken.None).ConfigureAwait(false); }
                finally { _startGate.Release(); }
            });
            using var configurationAdmission = CancellationTokenSource.CreateLinkedTokenSource(admission.Token, configuration?.Token ?? admission.Token);
            await _startGate.WaitAsync(configurationAdmission.Token).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (_jobs.Values.Count(item => item.ExecutionId == run.ExecutionId && !item.Backend.HasExited) >= 5)
                    { return new(false, null, "Too many background jobs (max 5). Kill or finish one first."); }
                }

                var session = await shellExecutorFactory.GetSessionAsync(configurationAdmission.Token).ConfigureAwait(false);
                var buffer = new BoundedOutputBuffer();
                var backend = await session.StartBackgroundAsync(command, buffer, configurationAdmission.Token).ConfigureAwait(false);
                job = new("shell-" + Guid.NewGuid().ToString("N")[..12], run.UserId, run.ExecutionId, backend, buffer, permission, configuration);
                lock (_gate) { _jobs.Add(job.Id, job); }

                transferred = true;
                _ = CompleteAsync(job);
                var worker = DelegatedWorkState.CurrentWorker;
                // A worker's jobs end with that worker, while the parent's jobs keep their conversation lifetime.
                worker?.OnFinished(() => RemoveAsync(item => ReferenceEquals(item, job), CancellationToken.None));
                run.DelegatedWork?.Watch(job.Id, worker?.Handle, backend.Completion);
                configurationAdmission.Token.ThrowIfCancellationRequested();
                return new(true, job.Id, $"Started background job '{job.Id}'.");
            }
            catch
            {
                if (job is not null) { await RemoveAsync(item => ReferenceEquals(item, job), CancellationToken.None).ConfigureAwait(false); }

                throw;
            }
            finally { _startGate.Release(); }
        }
        finally { if (!transferred) { configuration?.Dispose(); permission.Dispose(); } }
    }
    public async Task<ReadJobResult> ReadAsync(string jobId, long afterOffset, int? maxBytes, CancellationToken cancellationToken = default)
    {
        var job = Find(jobId);
        if (job is null) { return new(false, string.Empty, afterOffset, 0, "unknown", null, false, $"No such job '{jobId}'."); }

        await job.Backend.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var read = job.Buffer.ReadFrom(Math.Max(0, afterOffset), Math.Min(maxBytes is > 0 ? maxBytes.Value : 16 * 1024, 1024 * 1024));
        return new(true, read.Text, read.NewOffset, read.TotalSize, job.Backend.HasExited ? job.Killed ? "killed" : "exited" : "running",
            job.Backend.ExitCode, read.Truncated, $"Read background job '{jobId}'.");
    }
    public async Task<KillJobResult> KillAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var job = Find(jobId);
        if (job is null) { return new(false, false, $"No such job '{jobId}'."); }

        if (job.Backend.HasExited) { return new(true, false, $"Background job '{jobId}' has already exited."); }

        await job.Backend.StopAsync(cancellationToken).ConfigureAwait(false);
        job.Killed = true;
        return new(true, true, $"Stopped background job '{jobId}'.");
    }
    public Task CleanupExecutionAsync(string executionId, CancellationToken cancellationToken = default)
        => RemoveAsync(item => item.ExecutionId == executionId, cancellationToken);
    public Task CleanupUserAsync(string userId, CancellationToken cancellationToken = default)
        => RemoveAsync(item => item.UserId == userId, cancellationToken);
    public Task? FindCompletion(string jobId) => Find(jobId)?.Backend.Completion;
    public bool HasLiveJobs(string executionId)
    { lock (_gate) { return _jobs.Values.Any(job => job.ExecutionId == executionId && !job.Backend.HasExited); } }
    public async ValueTask DisposeAsync() { await RemoveAsync(_ => true, CancellationToken.None).ConfigureAwait(false); _startGate.Dispose(); }
    private Job? Find(string id)
    {
        var run = runContextAccessor.Current ?? throw new InvalidOperationException("Background jobs require an active run.");
        lock (_gate) { return _jobs.TryGetValue(id, out var job) && job.UserId == run.UserId && job.ExecutionId == run.ExecutionId ? job : null; }
    }
    private async Task RemoveAsync(Func<Job, bool> match, CancellationToken ct)
    {
        Job[] removed;
        lock (_gate) { removed = _jobs.Values.Where(match).ToArray(); }

        foreach (var job in removed)
        {
            try
            {
                if (!job.Backend.HasExited) { await job.Backend.StopAsync(ct).ConfigureAwait(false); }

                await job.Backend.DisposeAsync().ConfigureAwait(false);
                lock (_gate) { _jobs.Remove(job.Id); }

                job.Configuration?.Dispose();
                job.Permission.Dispose();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Background job {JobId} could not stop, its owner may retry cleanup.", job.Id); }
        }
    }
    private Task PruneAsync()
    {
        HashSet<string> excess;
        lock (_gate)
        {
            excess = _jobs.Values.Where(item => item.CompletedAt is not null).OrderByDescending(item => item.CompletedAt).Skip(25).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        }

        return RemoveAsync(item => excess.Contains(item.Id), CancellationToken.None);
    }
    // Completion owns retention even when no caller reads or starts another job.
    private async Task CompleteAsync(Job job)
    {
        try
        {
            await job.Backend.Completion.ConfigureAwait(false);
            lock (_gate) { job.CompletedAt = DateTimeOffset.UtcNow; }

            job.Configuration?.Dispose();
            job.Permission.Dispose();
            await PruneAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Background job {JobId} completion cleanup failed.", job.Id); }
    }
    private sealed record Job(string Id, string UserId, string ExecutionId, IExecutionBackgroundJob Backend,
        BoundedOutputBuffer Buffer, ExecutionPermissionRegistry.Registration Permission, IDisposable? Configuration)
    {
        public bool Killed { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
    }
}
