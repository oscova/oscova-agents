using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.ApiContracts.Chat;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Oscova.Agents.Host.Components.Prompts;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Host.Services;
using Oscova.Agents.Infrastructure.Chat;
using Oscova.Agents.Host.Artifacts;

namespace Oscova.Agents.Host.Components.Chat;

/// <summary>
/// Renders one assistant message and lets streaming deltas invalidate only that message.
/// </summary>
public sealed partial class BotMessageView : ComponentBase, IDisposable
{
    private static readonly ConditionalWeakTable<ChatDisplayMessage, BotMessageView> Subscribers = new();

    private bool _isDisposed;

    [Parameter, EditorRequired]
    public ChatDisplayMessage Message { get; set; } = default!;

    [Parameter] public ChatThread Thread { get; set; } = default!;
    [Parameter] public IReadOnlyList<ThreadArtifact>? Artifacts { get; set; }
    [Parameter] public EventCallback<UserQuestionAnswer> OnSubmitQuestions { get; set; }
    [Parameter] public EventCallback<UserQuestions.Card> OnDismissQuestions { get; set; }

    [Parameter]
    public string AssistantInitials { get; set; } = "AI";

    [Parameter]
    public string? AssistantPictureUrl { get; set; }

    [Parameter]
    public bool IsRunActive { get; set; }

    [Parameter]
    public bool IsLastMessage { get; set; }

    [Parameter]
    public bool IsReadOnly { get; set; }

    [Parameter]
    public EventCallback<ChatDisplayMessage> OnRegenerate { get; set; }

    [Parameter] public EventCallback<ChatDisplayMessage> OnBranch { get; set; }
    [Parameter] public bool IsBranching { get; set; }

    private string HistorySummarizedNotice => Loc.GetOrDefault(ChatTextKeys.HistorySummarizedNotice, "Earlier messages were summarized.");
    private string BranchLabel => Loc.GetOrDefault(ChatTextKeys.BranchLabel, "Branch in new chat");

    private bool CanBranch => !IsReadOnly && !IsRunActive &&
        Thread?.BranchableMessageIds.Contains(Message.MessageId, StringComparer.Ordinal) == true;

    [Parameter]
    public EventCallback<ToolApprovalChoice> OnToolApprovalResponse { get; set; }

    [Parameter]
    public EventCallback<BooleanElicitationChoice> OnBooleanElicitationResponse { get; set; }

    [Parameter]
    public EventCallback<StringElicitationChoice> OnStringElicitationResponse { get; set; }

    [Parameter]
    public EventCallback<NumberElicitationChoice> OnNumberElicitationResponse { get; set; }

    [Parameter]
    public EventCallback<EnumElicitationChoice> OnEnumElicitationResponse { get; set; }

    [Parameter]
    public Func<Task>? RequestScrollAsync { get; set; }

    [Parameter]
    public IReadOnlyList<string> AvatarEmotions { get; set; } = [];

    private string DisplayText(string text) => AvatarMarkupParser.StripForDisplay(text, AvatarEmotions);

    // Segment-edge whitespace can carry Markdown meaning, so clipboard text intentionally
    // bypasses transcript normalization.
    private string CopyText => string.Join(
        Environment.NewLine + Environment.NewLine,
        Message.Segments
            .Where(segment => segment.Type == ChatSegmentType.Text &&
                !string.IsNullOrWhiteSpace(segment.Text))
            .Select(segment => DisplayText(segment.Text)));

    private ChatSegment? FinalVisibleTextSegment => Message.Segments.LastOrDefault(
        segment => segment.Type == ChatSegmentType.Text && !string.IsNullOrWhiteSpace(segment.Text));

    private bool CanCopy => !string.IsNullOrWhiteSpace(CopyText) &&
        !(IsRunActive && IsLastMessage);

    [Inject]
    private IStringLocalizer<ChatText> Loc { get; set; } = default!;

    private string RegenerateLabel => Loc.GetOrDefault(ChatTextKeys.RegenerateLabel, "Regenerate");

    private bool CanRegenerate =>
        !IsReadOnly &&
        !Message.IsInteractionResponse &&
        (Thread?.DisplayMessages.Any(message => message.From == MessageOwner.User && !message.IsInteractionResponse) ?? true) &&
        IsLastMessage &&
        !IsRunActive &&
        FinalVisibleTextSegment is not null;

    private IEnumerable<RenderedBotItem> RenderedItems
    {
        get
        {
            var hasBotContent = false;

            if (Message.ToolApproval is not null)
            {
                hasBotContent = true;
                var approval = Message.ToolApproval;
                // Retained automatic decisions move into their actual tool activity, never disappear without it.
                var grouped = IsAutomaticApproval(approval) && !string.IsNullOrEmpty(approval.ToolCallId) &&
                    Thread?.DisplayMessages.Any(message => message.Segments.Any(segment =>
                        CanGroupTool(message, segment) && segment.FunctionCall?.CallId == approval.ToolCallId)) == true;
                if (!grouped) { yield return RenderedBotItem.ForToolApproval(approval); }
            }

            if (Message.Elicitation is not null)
            {
                hasBotContent = true;
                yield return RenderedBotItem.ForElicitation(Message.Elicitation);
            }

            List<ChatSegment>? tools = null;
            var startIndex = -1;
            for (var index = 0; index < Message.Segments.Count; index++)
            {
                var segment = Message.Segments[index];
                var isTool = segment.Type == ChatSegmentType.ToolCall;
                var isInteraction = !IsReadOnly && isTool && IsInteractionTool(segment.FunctionCall?.Name);
                var isUserQuestion = !IsReadOnly && isTool && UserQuestions.TryRead(Message, segment, out _);
                var isVisualization = Message.From == MessageOwner.Bot && isTool && ChatVisualizationService.TryRead(segment, out _);
                if (CanGroupTool(Message, segment))
                {
                    hasBotContent = true;
                    if (tools is null)
                    {
                        tools = [];
                        startIndex = index;
                    }

                    tools.Add(segment);
                    continue;
                }

                if (!isInteraction && !isUserQuestion && !isVisualization &&
                    segment.Type != ChatSegmentType.Think && string.IsNullOrWhiteSpace(segment.Text))
                {
                    continue;
                }

                if (tools is not null)
                {
                    yield return RenderedBotItem.ForTools(tools, startIndex);
                    tools = null;
                }
                // Dedicated interaction prompts own these calls, but the calls still break a group.
                hasBotContent = true;
                if (!isInteraction)
                {
                    yield return RenderedBotItem.ForSegment(segment);
                }
            }

            if (tools is not null)
            {
                yield return RenderedBotItem.ForTools(tools, startIndex);
            }

            if (!hasBotContent && IsRunActive && IsLastMessage)
            {
                yield return RenderedBotItem.Loading;
            }
        }
    }

    private static bool IsAutomaticApproval(ToolApprovalRequest approval) => approval.Outcome is
        ToolApprovalOutcome.AutoApproved or ToolApprovalOutcome.SessionPermission or ToolApprovalOutcome.FullAccess;

    /// <summary>Uses the same activity boundary when placing approval history and rendering tool calls.</summary>
    private bool CanGroupTool(ChatDisplayMessage message, ChatSegment segment) =>
        segment.Type == ChatSegmentType.ToolCall &&
        (IsReadOnly || (!IsInteractionTool(segment.FunctionCall?.Name) && !UserQuestions.TryRead(message, segment, out _))) &&
        !(message.From == MessageOwner.Bot && ChatVisualizationService.TryRead(segment, out _));

    private IReadOnlyList<ToolApprovalRequest> GetAutomaticApprovals(IReadOnlyList<ChatSegment> tools) =>
        Thread?.DisplayMessages.Select(message => message.ToolApproval)
            .OfType<ToolApprovalRequest>().Where(approval => IsAutomaticApproval(approval) &&
                !string.IsNullOrEmpty(approval.ToolCallId) && tools.Any(tool => tool.FunctionCall?.CallId == approval.ToolCallId))
            .ToArray() ?? [];

    /// <summary>Identifies calls represented by dedicated approval or input prompts.</summary>
    private static bool IsInteractionTool(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        var (_, tool) = ToolNaming.SplitQualifiedName(toolName);
        return tool is "request_approval" or "request_boolean_input"
            or "request_string_input" or "request_number_input" or "request_enum_input";
    }

    protected override void OnInitialized()
    {
        Subscribers.AddOrUpdate(Message, this);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender && IsRunActive && IsLastMessage && RequestScrollAsync is not null)
        {
            await RequestScrollAsync();
        }
    }

    public static void NotifyChanged(ChatDisplayMessage message)
    {
        if (Subscribers.TryGetValue(message, out var view) && !view._isDisposed)
        {
            _ = view.NotifyChangedAsync();
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        Subscribers.Remove(Message);
    }

    private bool IsCopyTarget(ChatSegment segment) =>
        CanCopy && ReferenceEquals(segment, FinalVisibleTextSegment);

    private async Task NotifyChangedAsync()
    {
        try
        {
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private sealed record RenderedBotItem(
        ChatSegment? Segment,
        ElicitationRequest? Elicitation,
        ToolApprovalRequest? ToolApproval,
        bool IsLoading,
        IReadOnlyList<ChatSegment>? Tools = null,
        int StartIndex = -1)
    {
        public static RenderedBotItem ForTools(IReadOnlyList<ChatSegment> tools, int startIndex) =>
            new(null, null, null, false, tools, startIndex);

        public static RenderedBotItem Loading { get; } = new(null, null, null, true);

        public static RenderedBotItem ForSegment(ChatSegment segment) => new(segment, null, null, false);

        public static RenderedBotItem ForElicitation(ElicitationRequest elicitation) => new(null, elicitation, null, false);

        public static RenderedBotItem ForToolApproval(ToolApprovalRequest approval) => new(null, null, approval, false);
    }
}
