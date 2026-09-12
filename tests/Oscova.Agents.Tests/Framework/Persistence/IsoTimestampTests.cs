using Oscova.Agents.Persistence;

namespace Oscova.Agents.Tests.Framework.Persistence;

public sealed class IsoTimestampTests
{
    [Fact]
    public void FormatSeconds_RoundTripsUtcSecondPrecision()
    {
        var value = new DateTimeOffset(2026, 4, 29, 12, 34, 56, 789, TimeSpan.Zero);

        var formatted = IsoTimestamp.FormatSeconds(value);
        var parsed = IsoTimestamp.Parse(formatted);

        Assert.Equal("2026-04-29T12:34:56Z", formatted);
        Assert.Equal(new DateTimeOffset(2026, 4, 29, 12, 34, 56, TimeSpan.Zero), parsed);
    }

    [Fact]
    public void FormatMilliseconds_RoundTripsUtcMillisecondPrecision()
    {
        var value = new DateTimeOffset(2026, 4, 29, 12, 34, 56, 789, TimeSpan.Zero);

        var formatted = IsoTimestamp.FormatMilliseconds(value);
        var parsed = IsoTimestamp.Parse(formatted);

        Assert.Equal("2026-04-29T12:34:56.789Z", formatted);
        Assert.Equal(value, parsed);
    }

    [Fact]
    public void FormatSeconds_NormalizesOffsetToUtc()
    {
        var value = new DateTimeOffset(2026, 4, 29, 18, 4, 56, TimeSpan.FromHours(5.5));

        var formatted = IsoTimestamp.FormatSeconds(value);
        var parsed = IsoTimestamp.Parse(formatted);

        Assert.Equal("2026-04-29T12:34:56Z", formatted);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Theory]
    [InlineData("2026-04-29T12:34:56Z")]
    [InlineData("2026-04-29T12:34:56.789Z")]
    public void Parse_AcceptsCanonicalUtcFormats(string value)
    {
        var parsed = IsoTimestamp.Parse(value);

        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Fact]
    public void Parse_RejectsNonCanonicalTimestamp()
    {
        Assert.Throws<FormatException>(() => IsoTimestamp.Parse("04/29/2026 12:34:56"));
    }
}