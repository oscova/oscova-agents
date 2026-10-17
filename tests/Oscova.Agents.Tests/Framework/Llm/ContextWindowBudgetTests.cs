using Oscova.Agents.Llm;

namespace Oscova.Agents.Tests.Framework.Llm;

public sealed class ContextWindowBudgetTests
{
    /// <summary>Evidence leaves room for other input and output without the history minimum floor.</summary>
    [Theory]
    [InlineData(10_000, 2_000, 1_000, 4_200)]
    [InlineData(2_000, 1_800, 400, 0)]
    [InlineData(2_000, 1_000, 500, 300)]
    [InlineData(0, 10_000, 1_000, ContextWindowBudget.DefaultHistoryBudget)]
    public void DeriveEvidenceBudget_AccountsForInputAndOutput(int context, int other, int output, int expected)
        => Assert.Equal(expected, ContextWindowBudget.DeriveEvidenceBudget(context, other, output));

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
