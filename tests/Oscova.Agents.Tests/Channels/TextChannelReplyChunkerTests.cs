using Oscova.Agents.Channels;

namespace Oscova.Agents.Tests.Channels;

public class TextChannelReplyChunkerTests
{
    [Fact]
    public void TextChannelReplyChunker_WhenBoundarySplitsSurrogatePair_KeepsValidChunks()
    {
        const int maxLength = 4096;
        var responseText = new string('x', maxLength - 1) + "😀tail";

        var chunks = TextChannelReplyChunker.Split(responseText, maxLength).ToArray();

        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, maxLength));
        Assert.All(chunks, chunk => Assert.False(char.IsHighSurrogate(chunk[^1])));
        Assert.All(chunks, chunk => Assert.False(char.IsLowSurrogate(chunk[0])));
        Assert.Equal(responseText, string.Concat(chunks));
    }

}
