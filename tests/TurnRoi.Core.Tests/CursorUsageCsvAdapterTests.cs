using TurnRoi.Core.Adapters;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Tests;

public class CursorUsageCsvAdapterTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cursor-usage.csv");

    private static Model.AgentSession Load() => new CursorUsageCsvAdapter().Read(FixturePath);

    [Fact]
    public void Groups_events_into_bursts_oldest_first()
    {
        var s = Load();

        Assert.Equal(2, s.Turns.Count);
        Assert.Single(s.Turns[0].Calls, _ => true);
        Assert.Equal(2, s.Turns[1].Calls.Count);
        Assert.Contains("burst of 2 requests", s.Turns[1].Prompt);
    }

    [Fact]
    public void Maps_token_columns_including_thousands_separators()
    {
        var call = Load().Turns[1].Calls[0];

        Assert.Equal("claude-4.5-sonnet-thinking", call.Model);
        Assert.Equal(1_000, call.Usage.Input);
        Assert.Equal(5_000, call.Usage.CacheWrite);
        Assert.Equal(50_000, call.Usage.CacheRead);
        Assert.Equal(900, call.Usage.Output);
    }

    [Fact]
    public void Uses_billed_cost_and_treats_included_as_zero()
    {
        var s = Load();
        var report = new SessionAnalyzer(PriceTable.LoadDefault()).Analyze(s);

        Assert.Equal(0.14m, report.TotalCost);
        Assert.Null(report.CorrectedSpend); // no prompts, so no pushback signal
        Assert.Empty(report.UnpricedModels);
        Assert.True(s.Notes.Any(n => n.Contains("included in your plan")));
    }

    [Fact]
    public void Derives_cache_writes_when_input_includes_them()
    {
        var lines = new[]
        {
            "Date,Model,Input (w/ Cache Write),Input (w/o Cache Write),Cache Read,Output Tokens,Total Tokens,Cost",
            "2026-10-26T10:00:00Z,gpt-5,8000,3000,1000,500,9500,0.02",
        };

        var call = new CursorUsageCsvAdapter().Parse(lines).Turns[0].Calls[0];

        Assert.Equal(3_000, call.Usage.Input);
        Assert.Equal(5_000, call.Usage.CacheWrite);
    }

    [Fact]
    public void Rejects_files_that_are_not_cursor_exports()
    {
        var ex = Record.Exception(() => new CursorUsageCsvAdapter().Parse(["Name,Amount", "a,1"]));

        Assert.IsType<FormatException>(ex);
    }

    [Fact]
    public void Splits_quoted_csv_fields()
    {
        Assert.Equal(new[] { "a", "1,000", "say \"hi\"", "" }, CursorUsageCsvAdapter.SplitCsv("a,\"1,000\",\"say \"\"hi\"\"\",").ToArray());
    }

    [Fact]
    public void Detects_cursor_csv()
    {
        Assert.Equal(AgentSource.Cursor, SessionSources.Detect(FixturePath));
    }
}
