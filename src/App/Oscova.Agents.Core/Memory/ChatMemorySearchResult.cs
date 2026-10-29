namespace Oscova.Agents.Core.Memory
{
    public sealed class ChatMemorySearchResult
    {
        public string Content { get; set; } = string.Empty;
        public string ThreadId { get; set; } = string.Empty;
        public DateTimeOffset TimestampUtc { get; set; }
        public double? Score { get; set; }
    }
}
