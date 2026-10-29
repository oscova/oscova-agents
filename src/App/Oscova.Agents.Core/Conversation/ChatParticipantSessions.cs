using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Conversations;

namespace Oscova.Agents.Core.Conversation;

/// <summary>Keeps shared-chat participants and their consumed transcript inside the owner's encrypted session.</summary>
public sealed class ChatParticipantSessions
{
    private const string StateKey = "Oscova.Chat.Participants";

    public IReadOnlyList<Participant> Participants { get; set; } = [];
    public string? LastRecipientId { get; set; }
    public JsonElement? TurnStartSession { get; set; }
    public string? TurnStartMessageId { get; set; }

    /// <summary>Loads participant state without creating a separate session store.</summary>
    public static ChatParticipantSessions Load(AgentSession session)
    {
        return session.StateBag.TryGetValue<ChatParticipantSessions>(StateKey, out var state) && state is not null
            ? state : new();
    }

    /// <summary>Reads routing metadata before the host restores the owning agent.</summary>
    public static ChatParticipantSessions Read(JsonElement? serialized)
    {
        if (serialized is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("stateBag", out var bag))
        {
            var state = AgentSessionStateBag.Deserialize(bag);
            if (state.TryGetValue<ChatParticipantSessions>(StateKey, out var participants) && participants is not null)
            {
                return participants;
            }
        }

        return new();
    }

    /// <summary>Saves cursor metadata with the nested native sessions in one archive.</summary>
    public void Save(AgentSession owner) => owner.StateBag.SetValue(StateKey, this);

    /// <summary>Finds an existing participant without admitting a new agent.</summary>
    public Participant? Find(string agentId) => Participants.FirstOrDefault(item => item.AgentId == agentId);

    /// <summary>Registers the owner or a user-authorized guest for this chat.</summary>
    public Participant Join(string agentId)
    {
        var existing = Find(agentId);
        if (existing is not null) { return existing; }

        var participant = new Participant { AgentId = agentId };
        Participants = [.. Participants, participant];
        return participant;
    }

    /// <summary>Projects only completed unseen exchanges, retaining matched tool payloads and speaker attribution.</summary>
    public static IReadOnlyList<ChatMessage> GetSharedMessages(
        ChatThread thread, Participant participant, string? currentMessageId = null)
    {
        var messages = new List<ChatMessage>();
        lock (thread.DisplayMessages)
        {
            foreach (var message in thread.DisplayMessages)
            {
                if (message.MessageId == currentMessageId || participant.SeenMessageIds.Contains(message.MessageId) ||
                    (message.IsInteractionResponse && !participant.QuestionOnlyMessageIds.Contains(message.MessageId)) ||
                    message.ToolApproval is not null || message.Elicitation is not null)
                {
                    continue;
                }
                // Pending calls can acquire a result later. Do not mark their display message consumed yet.
                if (message.Segments.Any(segment => segment.State != ChatSegmentState.Completed ||
                    segment.ExecutionState is ToolExecutionState.Pending or ToolExecutionState.Executing))
                {
                    continue;
                }

                participant.SeenMessageIds.Add(message.MessageId);
                var sourceAgentId = message.From == MessageOwner.User ? message.RecipientAgentId ?? thread.AgentId : message.AgentId;
                // Independent answers are visible here but were never added to the participant's native session.
                if (sourceAgentId == participant.AgentId && !participant.QuestionOnlyMessageIds.Remove(message.MessageId)) { continue; }

                var author = message.From == MessageOwner.User ? "User" : message.AgentName ?? message.AgentId ?? "Assistant";
                var text = ChatDisplayMessageFormatter.ComposeFiles(message.Text, message.WorkspaceFiles);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    messages.Add(new ChatMessage(ChatRole.User, $"[{author}]\n{text}") { MessageId = message.MessageId });
                }

                foreach (var segment in message.Segments)
                {
                    if (segment.FunctionCall is { } call && segment.FunctionResult is { } result)
                    {
                        // Quoted tool records are portable history, never executable pending calls for a different provider.
                        messages.Add(new ChatMessage(ChatRole.User,
                            $"[{author}: completed tool activity]\n{JsonSerializer.Serialize(new { call.CallId, call.Name, call.Arguments, result.Result })}"));
                    }
                }
            }
        }

        return messages;
    }

    /// <summary>Stores one agent's native session separately from its delegated work conversations.</summary>
    public sealed class Participant
    {
        public string AgentId { get; set; } = string.Empty;
        public JsonElement? Session { get; set; }
        public ISet<string> SeenMessageIds { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public ISet<string> QuestionOnlyMessageIds { get; set; } = new HashSet<string>(StringComparer.Ordinal);
    }
}
