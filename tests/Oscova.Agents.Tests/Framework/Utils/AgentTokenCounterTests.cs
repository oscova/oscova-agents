using Oscova.Agents.Utils;

namespace Oscova.Agents.Tests.Framework.Utils;

public sealed class AgentTokenCounterTests
{
    /// <summary>A long unbroken run counts in linear time. A single tokenizer call took about 100 seconds on this input.</summary>
    [Fact]
    public async Task CountTokens_LongUnbrokenRun_CompletesInLinearTime()
    {
        var counting = Task.Run(() => AgentTokenCounter.CountTokens(new string('a', 400_000)));

        var count = await counting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(50_000, count);
    }
}
