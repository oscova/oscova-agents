using Oscova.Agents.Conversations;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Policies;
using Oscova.Agents.Secrets;

namespace Oscova.Agents.Infrastructure.Conversations;

public sealed class ToolApprovalRouter(
    IWebToolApprovalService webToolApprovalService,
    ITextChannelToolApprovalService textChannelToolApprovalService,
    IRedactionService redactionService,
    ILogger<ToolApprovalRouter> logger) : IToolApprovalRouter
{
    /// <summary>Returns the not-found error for a skill script that cannot run, or null when approval is needed.</summary>
    public Func<string, string, CancellationToken, Task<string?>>? FindMissingSkillScriptAsync { get; set; }

    /// <summary>Resolves a review from trusted toolset data, or null to describe the call from its arguments.</summary>
    public Func<FunctionCallContent, CancellationToken, Task<ToolApprovalReview?>>? DescribeAsync { get; set; }

    public async Task<ToolApprovalSubmission> RouteAsync(
        ToolApprovalRequestContent request,
        ToolApprovalContext context,
        CancellationToken cancellationToken)
    {
        // A script that does not exist cannot run, so the model gets the framework's error instead of an approval card.
        if (FindMissingSkillScriptAsync is { } findMissing &&
            request.ToolCall is FunctionCallContent { Name: AgentSkillsProvider.RunSkillScriptToolName } call &&
            ToolApprovalRequestBuilder.TryGetString(call.Arguments, "skillName") is { } skillName &&
            ToolApprovalRequestBuilder.TryGetString(call.Arguments, "scriptName") is { } scriptName &&
            await findMissing(skillName, scriptName, cancellationToken).ConfigureAwait(false) is { } missing)
        {
            return AutoDecision(request, approved: false, missing);
        }

        if (context.Channel == ToolApprovalChannel.WebUI)
        {
            return await HandleWebAsync(request, context, cancellationToken).ConfigureAwait(false);
        }

        if (context.RunMode == ToolApprovalRunMode.AutoApproveRequired)
        {
            return AutoDecision(request, approved: true, "Auto-approved by run mode");
        }

        if (context.RunMode == ToolApprovalRunMode.AutoRejectRequired)
        {
            return AutoDecision(request, approved: false, "Auto-rejected by run mode");
        }

        return context.Channel switch
        {
            ToolApprovalChannel.Plugin =>
                await HandleTextAsync(request, context, cancellationToken).ConfigureAwait(false),
            ToolApprovalChannel.BackgroundTask => AutoDecision(request, approved: false, "Background task auto-rejected"),
            _ => throw new NotSupportedException($"Tool approval channel {context.Channel} is not supported.")
        };
    }

    private async Task<ToolApprovalSubmission> HandleWebAsync(
        ToolApprovalRequestContent request,
        ToolApprovalContext context,
        CancellationToken cancellationToken)
    {
        if (context.ChannelMetadata is not WebToolApprovalMetadata metadata)
        {
            logger.LogError("Missing WebToolApprovalMetadata for web UI tool approval.");
            return AutoDecision(request, approved: false, "Missing web metadata");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (webToolApprovalService.TryAutoApprove(request, metadata.ChatThread, metadata.OnStateChanged, context.RunMode) is { } automatic)
        {
            return automatic;
        }

        var displaySource = await CreateDisplaySourceAsync(
            request, context.UserId, cancellationToken).ConfigureAwait(false);

        // The displayed ID uniquely binds this card to its pending request across concurrent chats.
        var displayRequest = webToolApprovalService.RegisterToolApproval(
            request,
            displaySource,
            await DescribeCallAsync(request, cancellationToken).ConfigureAwait(false),
            metadata.ChatThread,
            metadata.OnStateChanged);

        return await webToolApprovalService
            .WaitForToolApprovalAsync(displayRequest.RequestId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ToolApprovalSubmission> HandleTextAsync(
        ToolApprovalRequestContent request,
        ToolApprovalContext context,
        CancellationToken cancellationToken)
    {
        if (context.ChannelMetadata is not TextChannelToolApprovalMetadata metadata)
        {
            logger.LogError("Missing TextChannelToolApprovalMetadata for {Channel} tool approval.", context.Channel);
            return AutoDecision(request, approved: false, "Missing text metadata");
        }

        var displaySource = await CreateDisplaySourceAsync(
            request, context.UserId, cancellationToken).ConfigureAwait(false);

        return await textChannelToolApprovalService.HandleApprovalAsync(
            request,
            displaySource,
            await DescribeCallAsync(request, cancellationToken).ConfigureAwait(false),
            metadata.ChannelOriginator,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolApprovalReview?> DescribeCallAsync(ToolApprovalRequestContent request, CancellationToken cancellationToken) =>
        DescribeAsync is { } describe && request.ToolCall is FunctionCallContent call
            ? await describe(call, cancellationToken).ConfigureAwait(false)
            : null;

    private async Task<ToolApprovalRequestContent> CreateDisplaySourceAsync(
        ToolApprovalRequestContent source,
        string userId,
        CancellationToken cancellationToken)
    {
        if (source.ToolCall is not FunctionCallContent { Arguments: not null } call) return source;

        var arguments = await redactionService
            .RedactArgumentsAsync(call.Arguments, userId, cancellationToken)
            .ConfigureAwait(false);

        // Approval displays use a redacted copy so execution retains the original arguments.
        return new ToolApprovalRequestContent(
            source.RequestId,
            new FunctionCallContent(call.CallId, call.Name, arguments));
    }

    private static ToolApprovalSubmission AutoDecision(ToolApprovalRequestContent request, bool approved, string reason)
    {
        var response = request.CreateResponse(approved, reason);
        return new ToolApprovalSubmission(response, null);
    }
}

public sealed class WebToolApprovalMetadata
{
    public required ChatThread ChatThread { get; init; }
    public required Action OnStateChanged { get; init; }
}

public sealed class TextChannelToolApprovalMetadata
{
    public required PluginElicitationOrigin ChannelOriginator { get; init; }
}
