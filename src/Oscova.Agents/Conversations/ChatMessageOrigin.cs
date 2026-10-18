using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Oscova.Agents.Conversations;

/// <summary>Distinguishes submitted requests from generated context across history replay and session reload.</summary>
public static class ChatMessageOrigin
{
    /// <summary>Returns whether a message is a user request rather than provider-generated context.</summary>
    public static bool IsSubmittedRequest(this ChatMessage message)
    {
        if (message.Role != ChatRole.User) { return false; }
        if (message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.AIContextProvider) { return false; }

        // Session serialization restores object-valued metadata as JSON rather than typed attribution.
        if (message.AdditionalProperties?.TryGetValue(AgentRequestMessageSourceAttribution.AdditionalPropertiesKey, out var origin) == true &&
            origin is JsonElement { ValueKind: JsonValueKind.Object } json &&
            (json.TryGetProperty(nameof(AgentRequestMessageSourceAttribution.SourceType), out var source) ||
             json.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(nameof(AgentRequestMessageSourceAttribution.SourceType)), out source)) &&
            source.ValueKind == JsonValueKind.Object &&
            (source.TryGetProperty(nameof(AgentRequestMessageSourceType.Value), out var value) ||
             source.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(nameof(AgentRequestMessageSourceType.Value)), out value)) &&
            value.ValueKind == JsonValueKind.String)
        {
            return !string.Equals(value.GetString(), AgentRequestMessageSourceType.AIContextProvider.Value, StringComparison.Ordinal);
        }

        return true;
    }

    // History retrieval stamps shallow message clones, so metadata needs its own dictionary first.
    internal static IEnumerable<ChatMessage> CopyHistoryMetadata(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            var copy = message.Clone();
            copy.AdditionalProperties = message.AdditionalProperties is null ? null : new(message.AdditionalProperties);
            yield return copy;
        }
    }
}
