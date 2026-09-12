using Oscova.Agents.Providers;

namespace Oscova.Agents.Tests.Framework.Providers;

public sealed class LexicalToolSearchStrategyTests
{
    [Fact]
    public void Search_RanksNameMatchesAboveDescriptionMatches()
    {
        var strategy = new LexicalToolSearchStrategy();
        ToolSearchCandidate[] candidates =
        [
            new("music.play", "Starts playback.", ["songName"]),
            new("media.lookup", "Finds music metadata.", ["query"])
        ];

        var result = strategy.Search("music", candidates, maxResults: 5);

        Assert.Equal(["music.play", "media.lookup"], result);
    }

    [Fact]
    public void Search_ExcludesNonMatchesAndCapsResultsDeterministically()
    {
        var strategy = new LexicalToolSearchStrategy();
        ToolSearchCandidate[] candidates =
        [
            new("beta.tool", "Finds calendar entries.", []),
            new("alpha.tool", "Finds calendar entries.", []),
            new("zeta.tool", "Finds calendar entries.", []),
            new("local.tool", "Reads local files.", [])
        ];

        var result = strategy.Search("calendar", candidates, maxResults: 2);

        Assert.Equal(["alpha.tool", "beta.tool"], result);
    }
}
