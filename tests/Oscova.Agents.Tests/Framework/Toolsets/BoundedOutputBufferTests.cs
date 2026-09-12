using System.Text;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Tests.Framework.Toolsets;

public sealed class BoundedOutputBufferTests
{
    [Fact]
    public void ReadFrom_BeforeCapacity_ReturnsOutputAfterAbsoluteOffset()
    {
        var buffer = new BoundedOutputBuffer(capacityBytes: 16);
        buffer.Append(Encoding.UTF8.GetBytes("alpha beta"));

        (string text, long newOffset, long totalSize, bool truncated) = buffer.ReadFrom(6, maxBytes: 4);

        Assert.Equal("beta", text);
        Assert.Equal(10, newOffset);
        Assert.Equal(10, totalSize);
        Assert.False(truncated);
        Assert.False(buffer.Truncated);
    }

    [Fact]
    public void Append_WhenCapacityExceeded_DropsMiddleAndKeepsAbsoluteOffsets()
    {
        var buffer = new BoundedOutputBuffer(capacityBytes: 10);
        buffer.Append(Encoding.UTF8.GetBytes("0123456789"));
        buffer.Append(Encoding.UTF8.GetBytes("ABCDEFGHIJ"));

        (string text, long newOffset, long totalSize, bool truncated) = buffer.ReadFrom(0, maxBytes: 20);

        Assert.Equal("01234FGHIJ", text);
        Assert.Equal(20, newOffset);
        Assert.Equal(20, totalSize);
        Assert.True(truncated);
        Assert.True(buffer.Truncated);
        Assert.Equal(20, buffer.TotalSize);
    }

    [Fact]
    public void ReadFrom_WhenOffsetIsInDroppedMiddle_ContinuesAtRetainedTail()
    {
        var buffer = new BoundedOutputBuffer(capacityBytes: 10);
        buffer.Append(Encoding.UTF8.GetBytes("0123456789"));
        buffer.Append(Encoding.UTF8.GetBytes("ABCDEFGHIJ"));

        (string text, long newOffset, long totalSize, bool truncated) = buffer.ReadFrom(5, maxBytes: 20);

        Assert.Equal("FGHIJ", text);
        Assert.Equal(20, newOffset);
        Assert.Equal(20, totalSize);
        Assert.True(truncated);
    }

    [Fact]
    public void Append_AfterTruncation_MovesTailForward()
    {
        var buffer = new BoundedOutputBuffer(capacityBytes: 10);
        buffer.Append(Encoding.UTF8.GetBytes("0123456789A"));
        buffer.Append(Encoding.UTF8.GetBytes("BC"));

        (string text, long newOffset, long totalSize, bool truncated) = buffer.ReadFrom(0, maxBytes: 20);

        Assert.Equal("0123489ABC", text);
        Assert.Equal(13, newOffset);
        Assert.Equal(13, totalSize);
        Assert.True(truncated);
    }

    [Fact]
    public void Append_FromMultipleThreads_TracksLogicalSize()
    {
        var buffer = new BoundedOutputBuffer(capacityBytes: 64);
        byte[] chunk = Encoding.UTF8.GetBytes("abcd");

        Parallel.For(0, 1000, _ => buffer.Append(chunk));

        Assert.Equal(4000, buffer.TotalSize);
        Assert.True(buffer.Truncated);
    }

    [Fact]
    public void Constructor_WithInvalidCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedOutputBuffer(0));
    }
}
