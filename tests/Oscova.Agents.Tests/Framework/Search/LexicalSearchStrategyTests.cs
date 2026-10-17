using Oscova.Agents.Search;

namespace Oscova.Agents.Tests.Framework.Search;

public sealed class LexicalSearchStrategyTests
{
    /// <summary>Evidence uses complete original tokens even when a clipping boundary splits a longer word.</summary>
    [Fact]
    public void FindTokenMatches_UsesOriginalLexicalBoundaries()
    {
        Assert.Equal([(8, 4), (12, 4), (17, 4), (22, 4)],
            LexicalSearchStrategy.FindTokenMatches("Fall Guys", "fallout FallGuys fall guys"));
        var boundaryText = new string('x', 395) + " fallout " + new string('x', 490) + " fall";
        Assert.Equal([(boundaryText.Length - 4, 4)], LexicalSearchStrategy.FindTokenMatches("fall", boundaryText));
    }

    [Fact]
    public void Search_RanksTitleMatchesAboveBodyMatches()
    {
        LexicalSearchDocument[] documents =
        [
            new("body", "media.lookup", "Finds music metadata."),
            new("title", "music.play", "Starts playback.")
        ];

        var result = LexicalSearchStrategy.Search("music", documents, maxResults: 5);

        Assert.Equal(["title", "body"], result);
    }

    [Fact]
    public void Search_UsesHighestWeightPerQueryToken()
    {
        LexicalSearchDocument[] documents =
        [
            new("b", "calendar.tool", "calendar entries"),
            new("a", "calendar.lookup")
        ];

        var result = LexicalSearchStrategy.Search("calendar", documents, maxResults: 5);

        Assert.Equal(["a", "b"], result);
    }

    [Fact]
    public void Search_IncludesKeywordsInBodyMatches()
    {
        LexicalSearchDocument[] documents =
        [
            new("files", "workspace.read", "Reads workspace content.", ["readLocalFiles"])
        ];

        var result = LexicalSearchStrategy.Search("local", documents, maxResults: 5);

        Assert.Equal(["files"], result);
    }

    [Fact]
    public void Search_ReturnsEmptyWhenNoSearchCanRun()
    {
        LexicalSearchDocument[] documents =
        [
            new("files", "workspace.read", "Reads workspace content.")
        ];

        Assert.Empty(LexicalSearchStrategy.Search(" ", documents, maxResults: 5));
        Assert.Empty(LexicalSearchStrategy.Search("files", [], maxResults: 5));
        Assert.Empty(LexicalSearchStrategy.Search("files", documents, maxResults: 0));
    }

    [Fact]
    public void Search_TieBreaksByIdAndRespectsMaxResults()
    {
        LexicalSearchDocument[] documents =
        [
            new("beta", "beta.tool", "Finds calendar entries."),
            new("Alpha", "alpha.tool", "Finds calendar entries."),
            new("zeta", "zeta.tool", "Finds calendar entries.")
        ];

        var result = LexicalSearchStrategy.Search("calendar", documents, maxResults: 2);

        Assert.Equal(["Alpha", "beta"], result);
    }
}
