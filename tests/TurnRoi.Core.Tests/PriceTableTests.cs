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

    [Theory]
    [InlineData("claude-4.5-sonnet-thinking", "claude-sonnet-4-5")]
    [InlineData("claude-4-opus", "claude-opus-4")]
    [InlineData("claude-4.5-haiku", "claude-haiku-4-5")]
    public void Prices_cursor_style_model_names(string cursorName, string canonical)
    {
        var prices = PriceTable.LoadDefault();
        Assert.NotNull(prices.Find(cursorName));
        Assert.Equal(prices.Find(canonical), prices.Find(cursorName));
    }

    [Theory]
    [InlineData("claude-opus-5-5", 4.00)]
    [InlineData("anthropic/claude-sonnet-5-5", 2.00)]
    [InlineData("claude-haiku-5-5", 0.10)]
    [InlineData("claude-opus-5-20260724", 5.00)]
    [InlineData("claude-haiku-4-5-20251001", 1.00)]
    [InlineData("gpt-5-codex", 1.25)]
    [InlineData("gpt-5.3-codex", 1.75)]
    [InlineData("claude-4.5-sonnet-thinking", 3.00)]
    public void Prices_current_models_on_version_boundaries(string model, double? expectedInput)
    {
        Assert.Equal(expectedInput is null ? null : (decimal)expectedInput.Value, _prices.Find(model)?.Input);
    }

    [Theory]
    [InlineData("gpt-5.7")]
    [InlineData("claude-opus-5-6")]
    [InlineData("claude-sonnet-6")]
    [InlineData("gpt-5.4-pro")]
    public void Newer_versions_do_not_borrow_an_older_price(string model)
    {
        Assert.Null(_prices.Find(model));
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
