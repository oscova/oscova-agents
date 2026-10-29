using System.Globalization;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Tasks;
using Oscova.Agents.FileBackups;
using Oscova.Agents.Infrastructure.Toolsets;
using Oscova.Agents.Llm;
using Oscova.Agents.Memory;
using Oscova.Agents.Secrets;
using Oscova.Agents.Tasks;
using Oscova.Agents.Templating;

namespace Oscova.Agents.Infrastructure.Memory;

/// <summary>Supplies the morning brief with the night's result and the to-do changes since the last delivered brief.</summary>
/// <remarks>The boundary is derived from retained runs. When none of them is a delivered brief, it falls back to 24 hours.</remarks>
public sealed class MorningBriefTaskHandler(IAgentTaskRepository taskRepository, IAgentTaskCatalog catalog,
    IAgentMemoryService memory, IUserDirectoryService userDirectories, IContentProtector protector,
    IUserFileBackupService fileBackups, RunContextAccessor runContextAccessor) : IAgentTaskHandler
{
    private const int DefaultKeepLastRuns = 30;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <inheritdoc />
    public async Task<AgentRunPlan?> PrepareAsync(AgentTaskDefinition task, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var limit = task.KeepLastRuns ?? DefaultKeepLastRuns;
        var ownRuns = await taskRepository.GetRunSummariesAsync(task.UserId, task.Id, limit, ct).ConfigureAwait(false);
        var boundary = ownRuns.FirstOrDefault(run => run.Status == AgentTaskStatus.Completed &&
            !string.IsNullOrWhiteSpace(run.ResultSummary))?.StartedAt ?? now.AddHours(-24);
        var night = await GetNightRunAsync(task, boundary, limit, ct).ConfigureAwait(false);

        // Retention runs before the model so the brief reports what it removed.
        var store = BacklogFile.CreateStore(task.UserId, userDirectories, protector, fileBackups);
        var items = new List<BacklogItem>();
        var retired = 0;
        foreach (var item in await BacklogFile.ReadItemsAsync(store, ct).ConfigureAwait(false))
        {
            if (item.State == BacklogFile.DoneState && item.Completed < now - Retention)
            {
                await store.DeleteAsync(item.Id, ct).ConfigureAwait(false);
                retired++;
                continue;
            }

            items.Add(item);
        }

        var changed = items.Any(item => item.Changed > boundary || item.Completed > boundary);
        if (night is null && !changed) { return null; }

        var (date, _) = await memory.ReadDailyLogAsync(task.UserId, daysAgo: 1, ct).ConfigureAwait(false);
        var log = await memory.GetStore(task.UserId).ReadConceptAsync(AgentMemoryService.DailyConceptId(date), ct).ConfigureAwait(false);
        var briefTitle = $"Morning brief, {date.AddDays(1).ToString("d MMMM", CultureInfo.InvariantCulture)}";

        var head = $"{NightLine(night)}\nRetired: {retired} done item(s) older than 30 days were removed.";
        var itemLines = new List<string>();
        Section(itemLines, "Completed since the last brief:", items.Where(item => item.State == BacklogFile.DoneState && item.Completed > boundary),
            item => $"- {item.Title} (conceptId: {item.Id}, assignee: {item.Assignee}): {item.Evidence}");
        Section(itemLines, "Open items delegated to the agent:", Open(items, BacklogFile.AgentAssignee), OpenLine);
        Section(itemLines, "Open items for the user:", Open(items, BacklogFile.UserAssignee), OpenLine);
        var logLines = new List<string> { $"Yesterday's daily log ({date:yyyy-MM-dd}):" };
        logLines.AddRange(log?.Body.Trim() is { Length: > 0 } body ? body.Split('\n').Select(line => line.TrimEnd('\r')) : ["- none"]);

        string Render(string evidence) => PromptTemplateRenderer.Render(task.Instruction, new { brief_title = briefTitle, evidence });
        var baseTokens = ContextWindowTokenEstimator.EstimateText(Render(string.Empty));
        int EstimateEvidence(string evidence) => Math.Max(ContextWindowTokenEstimator.EstimateText(evidence),
            ContextWindowTokenEstimator.EstimateText(Render(evidence)) - baseTokens);

        return new AgentRunPlan(Render(string.Empty), FinalizeAsync: (budget, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The daily log goes first from its end, then item lines from the end. The head is never cut.
            var lines = new List<string>([.. itemLines, .. logLines]);
            var omitted = 0;
            string Evidence() => string.Join('\n', [head, .. lines]) + (omitted > 0 ? $"\n{omitted} more line(s) did not fit." : string.Empty);
            while (lines.Count > 0 && EstimateEvidence(Evidence()) > budget)
            {
                lines.RemoveAt(lines.Count - 1);
                omitted++;
            }

            var evidence = Evidence();
            if (EstimateEvidence(evidence) > budget)
            {
                throw new InvalidOperationException("The model context has no room for the morning brief evidence. Increase the context allowance.");
            }

            var instruction = Render(evidence);
            if (!instruction.Contains(evidence, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The task instruction must include its complete evidence placeholder.");
            }

            return Task.FromResult(new AgentRunPlan(instruction, VerifyDeliveredAsync));
        });
    }

    // An undelivered brief fails the run, so the boundary holds and the next brief reports the same changes.
    private Task VerifyDeliveredAsync(CancellationToken ct)
    {
        var notify = ToolNaming.BuildToolsetQualifiedName<NotificationToolset>(nameof(NotificationToolset.NotifyUserAsync));
        var delivered = runContextAccessor.Current?.ToolInvocationLedger?.Snapshot()
            .Any(record => record.ToolName == notify && record.Succeeded > 0) ?? false;
        return delivered
            ? Task.CompletedTask
            : throw new InvalidOperationException("The morning brief was written but not delivered, because the notify tool never succeeded.");
    }

    private async Task<AgentTaskRun?> GetNightRunAsync(AgentTaskDefinition task, DateTimeOffset boundary, int limit, CancellationToken ct)
    {
        var nightTask = catalog.GetTasksForUser(task.UserId).FirstOrDefault(entry => entry.OwnerUserId == task.UserId &&
            Path.GetFileName(entry.DirectoryPath) == WellKnownTasks.NightShift);
        if (nightTask is null) { return null; }

        // Idle nights complete with no result and are not reported.
        return (await taskRepository.GetRunSummariesAsync(task.UserId, nightTask.TaskId, limit, ct).ConfigureAwait(false))
            .FirstOrDefault(run => run.StartedAt > boundary && (run.Status is AgentTaskStatus.Failed or AgentTaskStatus.Skipped ||
                (run.Status == AgentTaskStatus.Completed && !string.IsNullOrWhiteSpace(run.ResultSummary))));
    }

    private static string NightLine(AgentTaskRun? run) => run is null
        ? "Night Shift: no run since the last brief."
        : $"Night Shift: {run.Status} at {BacklogItem.Format(run.StartedAt)}. {(run.Status == AgentTaskStatus.Failed ? run.Error : run.ResultSummary)}".TrimEnd();

    private static IEnumerable<BacklogItem> Open(IEnumerable<BacklogItem> items, string assignee) =>
        items.Where(item => item.State == BacklogFile.OpenState && item.Assignee == assignee).OrderBy(item => item.Added ?? DateTimeOffset.MaxValue);

    private static string OpenLine(BacklogItem item) => $"- {item.Title} (conceptId: {item.Id}, added {BacklogItem.Format(item.Added)})";

    private static void Section(List<string> lines, string heading, IEnumerable<BacklogItem> items, Func<BacklogItem, string> line)
    {
        lines.Add(heading);
        var count = lines.Count;
        lines.AddRange(items.Select(line));
        if (lines.Count == count) { lines.Add("- none"); }
    }
}
