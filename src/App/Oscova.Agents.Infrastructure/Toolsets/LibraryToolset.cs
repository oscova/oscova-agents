using Microsoft.Extensions.Logging;
using Oscova.Agents.Core;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Library;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Memory;
using Oscova.Agents.Harness;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;
using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>Exposes Library search and indexing status only inside an authorized run.</summary>
public sealed class LibraryToolset(
    string userId,
    ILibraryIndexingService libraryIndexingService,
    PrivacySettings privacySettings,
    RunContextAccessor runContextAccessor,
    ILogger<LibraryToolset> logger) : IAgentToolset
{
    /// <summary>Searches authorized Library sources by semantic similarity.</summary>
    [DisplayName("search")]
    [AgentTool]
    [Description("Semantically search indexed files in the authorized Library workspace.")]
    public async Task<string> SearchLibraryAsync(
        [Description("What to find across Library files")] string query,
        [Description("Maximum number of results (default 5)")] int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ToolFailureException("A Library search query is required.");
        }

        RunContext? run = runContextAccessor.Current;
        if (run is null || !string.Equals(run.UserId, userId, StringComparison.Ordinal))
        {
            throw new ToolFailureException("Library Search requires an active authorized agent run.");
        }

        int resultLimit = maxResults <= 0 ? 5 : Math.Min(maxResults, 50);
        bool requireLocalEndpoint = run.AirGapped || privacySettings.IsLocalOnly;

        try
        {
            LibraryIndexStatus status = await libraryIndexingService.GetIndexStatusAsync(
                userId,
                requireLocalEndpoint,
                run.AllowedDirectories,
                cancellationToken).ConfigureAwait(false);
            if (status.State == LibraryIndexOverallState.Unavailable)
            {
                if (status.Message is { } reason) { return reason; }

                throw new ToolFailureException(
                    "Library Search is unavailable.",
                    "Configure an embedding provider and try again.");
            }

            var search = await libraryIndexingService.SearchAsync(
                userId,
                query,
                resultLimit,
                requireLocalEndpoint,
                run.AllowedDirectories,
                cancellationToken).ConfigureAwait(false);

            if (search.UnavailableReason is { } unavailable) { return unavailable; }

            var results = search.Matches;

            string output = results.Count == 0
                ? "No relevant Library files found."
                : string.Join(
                    Environment.NewLine + Environment.NewLine,
                    results.Select(result =>
                        $"{result.Content}{Environment.NewLine}{FormatCitation(result, run)}"));

            if (status.IsIncomplete)
            {
                output += results.Count == 0
                    ? Environment.NewLine
                    : Environment.NewLine + Environment.NewLine + Environment.NewLine;
                output += "Library indexing is incomplete; queued, indexing, or failed files may be missing from these results.";
            }

            return output.TrimEnd();
        }
        catch (LocalOnlyEmbeddingProviderBlockedException)
        {
            throw new ToolFailureException(
                "Library Search cannot use the configured embedding provider in local-only mode.",
                "Choose a local embedding provider or turn off local-only mode.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new ToolFailureException("Library workspace access is required.", "Grant Library workspace access and try again.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not ToolFailureException)
        {
            logger.LogError(ex, "Library search failed for user {UserId}.", userId);
            throw;
        }
    }

    /// <summary>Lists Library sources that are queued, indexing, or failed.</summary>
    [DisplayName("get_index_status")]
    [AgentTool]
    [Description("List Library files currently queued, indexing, or failed.")]
    public async Task<string> GetLibraryIndexStatusAsync(
        CancellationToken cancellationToken = default)
    {
        RunContext? run = runContextAccessor.Current;
        if (run is null || !string.Equals(run.UserId, userId, StringComparison.Ordinal))
        {
            throw new ToolFailureException("Library indexing status requires an active authorized agent run.");
        }

        try
        {
            bool requireLocalEndpoint = run.AirGapped || privacySettings.IsLocalOnly;
            LibraryIndexStatus status = await libraryIndexingService.GetIndexStatusAsync(
                userId,
                requireLocalEndpoint,
                run.AllowedDirectories,
                cancellationToken).ConfigureAwait(false);
            if (status.State == LibraryIndexOverallState.Unavailable)
            {
                throw new ToolFailureException(
                    "Library Search is unavailable.",
                    "Configure an embedding provider and try again.");
            }

            if (!status.IsIncomplete)
            {
                return "Library indexing is ready.";
            }

            var output = new StringBuilder();
            AppendPaths(output, "Queued", status.QueuedPaths, run);
            AppendPaths(output, "Indexing", status.IndexingPaths, run);
            AppendPaths(output, "Failed", status.FailedPaths, run);
            return output.Length == 0
                ? status.Message ?? "Library indexing is incomplete."
                : output.ToString().TrimEnd();
        }
        catch (UnauthorizedAccessException)
        {
            throw new ToolFailureException("Library workspace access is required.", "Grant Library workspace access and try again.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not ToolFailureException)
        {
            logger.LogError(ex, "Library indexing status failed for user {UserId}.", userId);
            throw;
        }
    }

    private static string FormatCitation(LibrarySearchResult result, RunContext run)
    {
        string page = result.PageNumber.HasValue
            ? $", Page: {result.PageNumber.Value.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
        string hostPath = PathHelper.ResolveAuthorizedHostPath(result.WorkspacePath, run.AllowedDirectories, WorkspacePathAccess.Read);
        return $"[Source: {PathHelper.ResolveToAgentPath(hostPath, run.AllowedDirectories)}{page}]";
    }

    private static void AppendPaths(StringBuilder output, string heading, IReadOnlyList<string> paths, RunContext run)
    {
        if (paths.Count == 0)
        {
            return;
        }

        output.Append(heading)
            .AppendLine(":");
        var library = run.AllowedDirectories.First(directory =>
            string.Equals(directory.Label, UserFileNames.LibraryWorkspaceDirectory, StringComparison.OrdinalIgnoreCase));
        foreach (string path in paths)
        {
            string hostPath = PathHelper.ResolveAuthorizedHostPath(Path.Combine(library.Value, path), run.AllowedDirectories, WorkspacePathAccess.Read);
            output.Append("- ")
                .AppendLine(PathHelper.ResolveToAgentPath(hostPath, run.AllowedDirectories));
        }
    }
}
