using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Oscova.Agents.Definitions;
using Oscova.Agents.Llm;
using Oscova.Agents.Host.Services;
using Oscova.Agents.Host.Localization;

namespace Oscova.Agents.Host.Components.Chat;

public partial class ChatPanel
{
    private string? _recipientAgentId;
    private string? _recipientThreadId;
    private int _recipientIndex;
    private bool _recipientMenuDismissed;
    private string? _pendingMention;
    private bool _pendingMentionStripped;

    private AgentDefinition? _selectedRecipient;
    private string? SelectedRecipientId => _recipientThreadId == ChatThread.Id ? _recipientAgentId : null;
    private AgentDefinition? SelectedRecipient => _recipientThreadId == ChatThread.Id
        ? CustomAgents.FirstOrDefault(agent => agent.Id == _recipientAgentId) ?? _selectedRecipient : null;

    private bool IsComposerModelLocked => SelectedRecipient is { UseCallerModelForConsultations: false } recipient &&
        recipient.Id != ActiveAgentDefinition.Id;

    // Displaying a guest's provider must not replace the owner's captured composer selection.
    private ModelProviderConfig? ComposerProvider
    {
        get
        {
            var recipient = SelectedRecipient ?? ActiveAgentDefinition;
            var providerId = AgentSelectionResolver.ResolveModelProviderId(
                ActiveAgentDefinition, recipient, ResolveSelectedProviderId()?.ToString(), DefaultModelProviderId?.ToString());
            var id = Guid.TryParse(providerId, out var parsed) ? parsed : DefaultModelProviderId;
            return ModelProviders.FirstOrDefault(provider => provider.Id == id);
        }
    }

    private string? ComposerModelExplanation => IsComposerModelLocked
        ? string.Format(Loc[ChatTextKeys.RecipientOwnModelProvider], SelectedRecipient!.Name) : null;

    private bool IsRecipientMenuOpen => ChatThread.DisplayMessages.Count > 0 && SelectedRecipient is null &&
        !_recipientMenuDismissed && TextWithoutPendingMention is ['@', ..] text && !text.Any(char.IsWhiteSpace);

    private IReadOnlyList<AgentDefinition> RecipientCandidates => CustomAgents
        .Where(agent => agent.Id != ActiveAgentDefinition.Id &&
            agent.Name.Contains(TextWithoutPendingMention is ['@', .. var query] ? query : string.Empty, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    private async Task SelectRecipientAsync(AgentDefinition agent)
    {
        _recipientAgentId = agent.Id;
        _selectedRecipient = agent;
        Drafts.SetRecipient(ChatThread.Id, agent);
        _recipientThreadId = ChatThread.Id;
        _recipientMenuDismissed = false;
        _recipientIndex = 0;
        OnComposerInputChanged();
        // Only the browser strips the mention: any server write to the bound value can overwrite keystrokes still in flight.
        // Until the next input event shows the strip, the server's copy may still start with the mention.
        var mention = _pendingMention = _inputText;
        _pendingMentionStripped = false;
        _utilsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/utils.js");
        await _utilsModule.InvokeVoidAsync("removeLeadingText", "chat-input", mention);
        // Input events from before the strip have all arrived, so the next one carries the browser's real value.
        if (_pendingMention == mention) { _pendingMentionStripped = true; }

        await RefreshContextSelectionAsync();
        await RefreshBudgetAsync();
    }

    // Runs on each input event: one after the strip, or one without the mention, replaces the stale copy.
    private void ForgetStrippedMention()
    {
        if (_pendingMention is { } mention &&
            (_pendingMentionStripped || !_inputText.StartsWith(mention, StringComparison.Ordinal)))
        {
            _pendingMention = null;
        }
    }

    private string TextWithoutPendingMention => _pendingMention is { } mention && _inputText.StartsWith(mention, StringComparison.Ordinal)
        ? _inputText[mention.Length..] : _inputText;

    // Text that is sent before the browser's strip shows up must not carry the mention.
    // Returns false when only the mention remains: the bound value stays untouched, since writing it can drop keystrokes in flight.
    // The mention stays pending, because input events sent before the strip can still carry it.
    private bool RemovePendingMention()
    {
        var text = TextWithoutPendingMention;
        if (text.Length == _inputText.Length) { return true; }

        if (string.IsNullOrWhiteSpace(text) && _pendingAttachments.Count == 0) { return false; }

        _inputText = text;
        return true;
    }

    private async Task RemoveRecipientAsync()
    {
        var hadRecipient = SelectedRecipientId is not null;
        Drafts.SetRecipient(ChatThread.Id, null);
        _recipientAgentId = null;
        _selectedRecipient = null;
        _recipientThreadId = null;
        if (hadRecipient)
        {
            await RefreshContextSelectionAsync();
            await RefreshBudgetAsync();
        }
    }

    private async Task RestoreRecipientAsync(string threadId, string? recipientId)
    {
        if (recipientId is null) { return; }

        var recipient = CustomAgents.FirstOrDefault(agent => agent.Id == recipientId)
            ?? new AgentDefinition { Id = recipientId, Name = recipientId };
        Drafts.SetRecipient(threadId, recipient);
        if (ChatThread.Id != threadId) { return; }

        _recipientAgentId = recipientId;
        _recipientThreadId = threadId;
        _selectedRecipient = recipient;
        await RefreshContextSelectionAsync();
        await RefreshBudgetAsync();
    }

    private async Task<bool> HandleRecipientKeyAsync(KeyboardEventArgs args)
    {
        if (!IsRecipientMenuOpen) { return false; }

        var candidates = RecipientCandidates;
        switch (args.Key)
        {
            case "Escape":
                _recipientMenuDismissed = true;
                return true;
            case "ArrowDown":
                _recipientIndex = candidates.Count == 0 ? 0 : (_recipientIndex + 1) % candidates.Count;
                return true;
            case "ArrowUp":
                _recipientIndex = candidates.Count == 0 ? 0 : (_recipientIndex + candidates.Count - 1) % candidates.Count;
                return true;
            case "Enter" when !args.ShiftKey:
            case "Tab":
                if (candidates.Count > 0) { await SelectRecipientAsync(candidates[Math.Min(_recipientIndex, candidates.Count - 1)]); }

                return true;
            default:
                return false;
        }
    }
}
