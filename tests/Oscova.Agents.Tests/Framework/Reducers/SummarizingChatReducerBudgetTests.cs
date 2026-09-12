using Oscova.Agents.Reducers;

namespace Oscova.Agents.Tests.Framework.Reducers;

public sealed class SummarizingChatReducerBudgetTests
{
    [Fact]
    public void DeriveHistoryBudget_WhenWindowUnknown_ReturnsDefault()
    {
        var budget = SummarizingChatReducer.DeriveHistoryBudget(0, null);

        Assert.Equal(SummarizingChatReducer.DefaultMaxTokenCount, budget);
    }

    [Fact]
    public void DeriveHistoryBudget_WhenWindowSmall_FloorsAtMinimum()
    {
        var budget = SummarizingChatReducer.DeriveHistoryBudget(4096, null);

        Assert.Equal(SummarizingChatReducer.MinimumHistoryBudget, budget);
    }

    [Fact]
    public void DeriveHistoryBudget_WhenOutputTokensExplicit_RespectsReserve()
    {
        var budget = SummarizingChatReducer.DeriveHistoryBudget(10000, 1000);

        Assert.Equal(5400, budget);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DeriveHistoryBudget_WhenOutputTokensNonPositive_UsesDefaultReserve(int maxOutputTokens)
    {
        var budget = SummarizingChatReducer.DeriveHistoryBudget(10000, maxOutputTokens);

        Assert.Equal(4500, budget);
    }

    [Fact]
    public void DeriveHistoryBudget_WhenWindowLarge_ScalesAfterOutputReserve()
    {
        var budget = SummarizingChatReducer.DeriveHistoryBudget(128000, null);

        Assert.Equal(74342, budget);
    }
}
