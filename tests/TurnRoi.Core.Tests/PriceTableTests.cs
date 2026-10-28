using TurnRoi.Core.Model;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Tests;

public class PriceTableTests
{
    private readonly PriceTable _prices = PriceTable.LoadDefault();

    [Fact]
    public void Matches_the_longest_prefix()
    {
        Assert.Equal(5.00m, _prices.Find("claude-opus-4-5-20251101")!.Input);
        Assert.Equal(15.00m, _prices.Find("claude-opus-4-1-20250805")!.Input);
        Assert.Equal(15.00m, _prices.Find("claude-opus-4-20250514")!.Input);
    }

    [Fact]
    public void Treats_dotted_model_versions_like_hyphenated_ones()
    {
        Assert.Equal(5.00m, _prices.Find("claude-opus-4.5")!.Input);
        Assert.Equal(3.00m, _prices.Find("claude-sonnet-4.5")!.Input);
        Assert.Equal(0.25m, _prices.Find("gpt-5-mini")!.Input);
        Assert.Equal(1.25m, _prices.Find("gpt-5")!.Input);
    }

    [Fact]
    public void Unknown_models_are_unpriced_not_guessed()
    {
        Assert.Null(_prices.Find("some-future-model"));
        var calls = new[] { new LlmCall("x", "some-future-model", null, new TokenUsage(1, 1, 0, 0), false) };
        Assert.Null(_prices.Cost(calls));
    }

    [Fact]
    public void Computes_cost_from_all_four_token_types()
    {
        var price = new ModelPrice(Input: 3m, Output: 15m, CacheWrite: 3.75m, CacheRead: 0.30m);
        var cost = price.Cost(new TokenUsage(1_000_000, 1_000_000, 1_000_000, 1_000_000));
        Assert.Equal(22.05m, cost);
    }
}
