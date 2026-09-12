using Microsoft.Extensions.AI;

namespace Oscova.Agents.Tests.Framework.Context
{
    // TODO: Rewrite for InMemoryChatHistoryProvider — CreateMessagesHistory and InteractionContext removed with OscovaCrew.
    public class ContextWindowLimitTests
    {
        [Fact]
        public void CreateMessagesHistory_Respects_ContextTokenLimit_Truncates_OlderMessages()
        {
        }

        [Fact]
        public void CreateMessagesHistory_Includes_All_When_Limit_Sufficient()
        {
        }
    }

    internal static class ChatMessageListExtensions
    {
        public static int FindIndex(this IList<ChatMessage> list, Func<ChatMessage, bool> predicate)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (predicate(list[i])) return i;
            }
            return -1;
        }
    }
}