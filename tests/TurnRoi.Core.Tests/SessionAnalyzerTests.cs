using TurnRoi.Core.Adapters;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Model;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Tests;

public class SessionAnalyzerTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-code-basic.jsonl");

    private static SessionReport AnalyzeFixture() =>
        new SessionAnalyzer(PriceTable.LoadDefault()).Analyze(new ClaudeCodeJsonlAdapter().Read(FixturePath));

    [Fact]
    public void Flags_rereading_an_unchanged_file_but_not_reading_after_an_edit()
    {
        var report = AnalyzeFixture();

        var reread = Assert.Single(report.Findings, f => f.Kind == FindingKind.RepeatedRead);
        Assert.Equal(1, reread.TurnIndex);
    }

    [Fact]
    public void Flags_oversized_tool_output()
    {
        var finding = Assert.Single(AnalyzeFixture().Findings, f => f.Kind == FindingKind.OversizedToolResult);

        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal(22_500, finding.EstimatedTokens);
    }

    [Fact]
    public void Attributes_pushback_to_the_previous_turn_and_reports_its_spend()
    {
        var report = AnalyzeFixture();

        Assert.True(report.Turns.Single(t => t.Turn.Index == 1).WasCorrected);
        Assert.False(report.Turns.Single(t => t.Turn.Index == 2).WasCorrected);
        Assert.NotNull(report.CorrectedSpend);
        Assert.True(report.CorrectedSpend > 0);
        Assert.True(report.CorrectedSpend < report.TotalCost);
    }

    [Fact]
    public void Prices_every_call_in_the_fixture()
    {
        var report = AnalyzeFixture();

        Assert.Empty(report.UnpricedModels);
        Assert.NotNull(report.TotalCost);
    }

    [Theory]
    [InlineData("No, that's wrong", true)]
    [InlineData("that didn't work", true)]
    [InlineData("It's still failing on CI", true)]
    [InlineData("[Request interrupted by user]", true)]
    [InlineData("revert the last change", true)]
    [InlineData("Now add logging", false)]
    [InlineData("Notice the null check in Program.cs", false)]
    [InlineData("Looks good, ship it", false)]
    [InlineData("nope", true)]
    [InlineData("No. Use the existing helper", true)]
    [InlineData("Stop. You're editing the wrong file", true)]
    [InlineData("stop changing the tests", true)]
    [InlineData("Wait, that's not what I asked for", true)]
    [InlineData("The build doesn't compile anymore", true)]
    [InlineData("You deleted the null check", true)]
    [InlineData("I said only the controller", true)]
    [InlineData("please don't touch the migrations", true)]
    [InlineData("Same error as before", true)]
    [InlineData("this is still broken", true)]
    [InlineData("No worries, now add logging", false)]
    [InlineData("No need to add tests for that", false)]
    [InlineData("No tests exist for RefundService yet, add some", false)]
    [InlineData("Stop the dev server and run the migrations", false)]
    [InlineData("nope, looks good", false)]
    [InlineData("Undocumented APIs: list them", false)]
    [InlineData("Wrap the call in a retry", false)]
    public void Detects_pushback_prompts(string prompt, bool expected)
    {
        Assert.Equal(expected, SessionAnalyzer.IsCorrection(prompt));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void A_pasted_build_error_is_pushback_only_after_an_edit(bool previousEdited, bool expected)
    {
        const string paste = "src/Claims.cs(12,5): error CS1002: ; expected\nBuild FAILED.";
        Assert.Equal(expected, SessionAnalyzer.IsCorrection(paste, previousEdited));
    }

    [Fact]
    public void Flags_low_cache_hit_after_the_first_turn()
    {
        var session = new AgentSession { SessionId = "s", Source = "test" };
        var t1 = new Turn { Index = 1, Prompt = "first" };
        t1.Calls.Add(new LlmCall("a", "claude-sonnet-4-5", null, new TokenUsage(10, 10, 50_000, 0), false));
        var t2 = new Turn { Index = 2, Prompt = "second" };
        t2.Calls.Add(new LlmCall("b", "claude-sonnet-4-5", null, new TokenUsage(10, 10, 40_000, 10_000), false));
        session.Turns.Add(t1);
        session.Turns.Add(t2);

        var report = new SessionAnalyzer(PriceTable.LoadDefault()).Analyze(session);

        var finding = Assert.Single(report.Findings, f => f.Kind == FindingKind.LowCacheHit);
        Assert.Equal(2, finding.TurnIndex);
    }
}
