using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Core;
using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>
/// Exposes bounded egress audit pages for dashboard and inspector-style review.
/// </summary>
[ApiController]
[Authorize]
[Route("api/egress-audit")]
[IgnoreAntiforgeryToken]
public sealed class EgressAuditController(
    IEgressAuditStore auditStore,
    UserManager<ApplicationUser> userManager,
    UserServiceManager userServiceManager,
    IAgentCatalog agentCatalog) : ControllerBase
{
    private const int MaxPageSize = 100;

    [HttpGet]
    public async Task<ActionResult<EgressAuditPage>> Get(
        [FromQuery] string scope = "self",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] DateTimeOffset? fromUtc = null,
        [FromQuery] DateTimeOffset? toUtc = null,
        [FromQuery] string? kind = null,
        [FromQuery] string? source = null,
        [FromQuery] string? searchTerm = null,
        CancellationToken ct = default)
    {
        var currentUserId = userManager.GetUserId(User);
        if (string.IsNullOrWhiteSpace(currentUserId))
        {
            return Unauthorized();
        }

        var labels = UserLabelMap.Create(await userServiceManager.GetOrCreateScopeAsync(currentUserId, ct), agentCatalog);
        var request = new EgressAuditQueryRequest
        {
            Page = page,
            PageSize = pageSize,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Kind = kind,
            Source = source,
            SearchTerm = searchTerm
        };

        if (string.Equals(scope, "self", StringComparison.OrdinalIgnoreCase))
        {
            var selfPage = await auditStore.QueryAsync(currentUserId, request with { SearchIds = labels.FindIds(searchTerm) }, ct);
            return Ok(selfPage with { Entries = [.. selfPage.Entries.Select(entry => labels.Label(entry.Record))] });
        }

        if (!string.Equals(scope, "system", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { Message = "Unknown egress audit scope." });
        }

        if (!User.IsInRole("Admin"))
        {
            return Forbid();
        }

        return Ok(await QuerySystemAsync(currentUserId, labels, request, ct));
    }

    private async Task<EgressAuditPage> QuerySystemAsync(
        string currentUserId,
        UserLabelMap labels,
        EgressAuditQueryRequest request,
        CancellationToken ct)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);
        var users = await userManager.Users
            .Where(user => user.Id != WellKnownUsers.SystemId)
            .OrderBy(user => user.UserName)
            .ToListAsync(ct);
        var entries = new List<EgressAuditEntry>();
        var storeRequest = request with { SearchTerm = null };
        var systemView = labels.ForSystemView();

        foreach (var user in users)
        {
            ct.ThrowIfCancellationRequested();
            var displayName = GetOwnerDisplayName(user);
            var records = await auditStore.QueryRecordsAsync(user.Id, storeRequest, ct);
            foreach (var record in records)
            {
                // Owner labels are attached at the API boundary so one shared UI can render self and system audit rows.
                entries.Add(user.Id == currentUserId
                    ? labels.Label(record, user.Id, displayName)
                    : ProjectForOperator(record, systemView, user.Id, displayName));
            }
        }

        var filtered = entries
            .Where(entry => MatchesSearch(entry, request.SearchTerm))
            .OrderByDescending(entry => entry.Record.Timestamp)
            .ToArray();

        return new EgressAuditPage
        {
            Entries = [.. filtered.Skip((page - 1) * pageSize).Take(pageSize)],
            CurrentPage = page,
            PageSize = pageSize,
            TotalCount = filtered.Length,
            TotalPages = Math.Max(1, (int)Math.Ceiling(filtered.Length / (double)pageSize)),
            Summary = BuildSummary(filtered)
        };
    }

    /// <summary>
    /// Shows the operator another user's row without that user's internals: no conversation, run or task ids,
    /// no agent, and only operator-shared providers and installed tools by name.
    /// </summary>
    private static EgressAuditEntry ProjectForOperator(EgressAuditRecord record, UserLabelMap systemView, string userId, string displayName)
    {
        var isShared = systemView.IsSharedProvider(record.ProviderId);
        var toolLabel = record.ToolName is null && record.McpServerId is null
            ? null
            : systemView.Tool(record.ToolName, record.McpServerId, null);
        return new EgressAuditEntry
        {
            Record = new EgressAuditRecord
            {
                Timestamp = record.Timestamp,
                Kind = record.Kind,
                Source = record.Source,
                ProviderId = isShared ? record.ProviderId : null,
                ModelId = isShared ? record.ModelId : null,
                ToolName = toolLabel is null or RecordLabelKeys.UsersOwnTools ? null : record.ToolName
            },
            OwnerUserId = userId,
            OwnerDisplayName = displayName,
            ProviderLabel = record.ProviderId is null ? null : isShared ? systemView.Provider(record.ProviderId) : RecordLabelKeys.OwnDestination,
            ToolLabel = toolLabel
        };
    }

    private static bool MatchesSearch(EgressAuditEntry entry, string? searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return true;
        }

        // Projected rows hold only what the operator may see, so search cannot reach hidden fields.
        var term = searchTerm.Trim();
        var record = entry.Record;
        return Contains(entry.OwnerUserId, term) ||
               Contains(entry.OwnerDisplayName, term) ||
               Contains(entry.AgentLabel, term) ||
               Contains(entry.ProviderLabel, term) ||
               Contains(entry.ToolLabel, term) ||
               Contains(record.ThreadId, term) ||
               Contains(record.RunId, term) ||
               Contains(record.TaskId, term) ||
               Contains(record.AgentId, term) ||
               Contains(record.ProviderId, term) ||
               Contains(record.McpServerId, term) ||
               Contains(record.ModelId, term) ||
               Contains(record.ToolName, term) ||
               Contains(record.Kind, term) ||
               Contains(record.Source, term);
    }

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static EgressAuditSummary BuildSummary(IReadOnlyCollection<EgressAuditEntry> entries)
    {
        var providerCount = entries.Count(entry => string.Equals(entry.Record.Kind, EgressAuditKinds.Provider, StringComparison.OrdinalIgnoreCase));
        var toolCount = entries.Count(entry => string.Equals(entry.Record.Kind, EgressAuditKinds.Tool, StringComparison.OrdinalIgnoreCase));
        var embeddingCount = entries.Count(entry => string.Equals(entry.Record.Kind, EgressAuditKinds.Embedding, StringComparison.OrdinalIgnoreCase));

        return new EgressAuditSummary(entries.Count, providerCount, toolCount, embeddingCount);
    }

    private static string GetOwnerDisplayName(ApplicationUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.FullName))
        {
            return user.FullName;
        }

        return string.IsNullOrWhiteSpace(user.UserName)
            ? user.Id
            : user.UserName;
    }
}
