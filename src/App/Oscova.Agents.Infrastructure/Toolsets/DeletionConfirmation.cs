using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Harness;
using static ModelContextProtocol.Protocol.ElicitRequestParams;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>Shares the confirmed-delete contract of the memory, notes and backlog forget tools.</summary>
internal static class DeletionConfirmation
{
    /// <summary>Requires exactly one of an ID or a scope, and returns the normalized scope or null for an ID.</summary>
    public static string? RequireOneTarget(string? conceptId, string? scope, params string[] scopes)
    {
        if (conceptId is null == scope is null)
        {
            throw new ToolFailureException("Supply exactly one of conceptId or scope.", "Use conceptId for one entry or scope for several.");
        }

        var normalized = scope?.Trim().ToLowerInvariant();
        if (normalized is not null && !scopes.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ToolFailureException($"scope must be one of: {string.Join(", ", scopes)}.");
        }

        return normalized;
    }

    /// <summary>Names the counted target of a confirmation card: "1 to-do item" for one, "all 3 to-do items" for several.</summary>
    public static string Count(int count, string singular, string plural, bool all = true) =>
        count == 1 ? $"1 {singular}" : $"{(all ? "all " : string.Empty)}{count} {plural}";

    /// <summary>Returns true only when the user explicitly confirms the deletion.</summary>
    public static async Task<bool> ConfirmAsync(RunContextAccessor runContextAccessor, IElicitationRouter elicitationRouter,
        string message, CancellationToken ct)
    {
        var context = runContextAccessor.Current?.Elicitation
            ?? throw new ToolFailureException("Deletion requires an active user confirmation context.");
        var response = await elicitationRouter.RouteElicitationAsync(new ElicitRequestParams
        {
            Message = message,
            RequestedSchema = new RequestSchema { Properties = { ["Answer"] = new BooleanSchema() } }
        }, context, ct).ConfigureAwait(false);
        if (response.Action == "accept" && response.Content is not null &&
            response.Content.TryGetValue("Answer", out var answer) && answer.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        // A declined deletion ends tool execution before the model can substitute another write.
        if (FunctionInvokingChatClient.CurrentContext is { } invocation) { invocation.Terminate = true; }

        return false;
    }
}
