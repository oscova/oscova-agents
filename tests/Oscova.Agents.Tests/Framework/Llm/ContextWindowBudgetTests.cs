using Oscova.Agents.Llm;

namespace Oscova.Agents.Tests.Framework.Llm;

public sealed class ContextWindowBudgetTests
{
    [Fact]
    public void DeriveOutputReserve_WhenWindowUnknown_ReturnsZero()
    {
        var reserve = ContextWindowBudget.DeriveOutputReserve(0, null);

        Assert.Equal(0, reserve);
    }

    [Fact]
    public void DeriveOutputReserve_WhenMaxOutputTokensExplicit_UsesConfiguredReserve()
    {
        var reserve = ContextWindowBudget.DeriveOutputReserve(10_000, 1_000);

        Assert.Equal(1_000, reserve);
    }

    [Fact]
    public void DeriveOutputReserve_WhenMaxOutputTokensExceedsWindow_ClampsToWindow()
    {
        var reserve = ContextWindowBudget.DeriveOutputReserve(4_096, 8_192);

        Assert.Equal(4_096, reserve);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void DeriveOutputReserve_WhenMaxOutputTokensMissingOrNonPositive_UsesAutomaticReserve(int? maxOutputTokens)
    {
        var reserve = ContextWindowBudget.DeriveOutputReserve(128_000, maxOutputTokens);

        Assert.Equal(ContextWindowBudget.MaximumOutputReserve, reserve);
    }

    [Fact]
    public void DeriveHistoryBudget_WhenWindowKnown_LeavesRoomForReserveAndPromptSurface()
    {
        var budget = ContextWindowBudget.DeriveHistoryBudget(10_000, 1_000);

        Assert.Equal(5_400, budget);
    }
}
