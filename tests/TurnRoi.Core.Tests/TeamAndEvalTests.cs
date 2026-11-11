using TurnRoi.Core.Adapters;
using TurnRoi.Core.Analysis;
using TurnRoi.Core.Evaluation;
using TurnRoi.Core.Pricing;
using TurnRoi.Core.Team;

namespace TurnRoi.Core.Tests;

public class TeamTests
{
    private static SessionReport Fixture() =>
        new SessionAnalyzer(PriceTable.LoadDefault()).Analyze(
            new ClaudeCodeJsonlAdapter().Read(Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-code-basic.jsonl")));

    [Fact]
    public void Export_holds_numbers_but_no_prompts_paths_or_ids()
    {
        var report = Fixture();
        var json = TeamExport.Build("alex", [report], hashProjects: false, DateTimeOffset.UnixEpoch).ToJson();

        Assert.Contains("\"project\": \"claims-api\"", json);
        Assert.DoesNotContain("/repo", json);
        Assert.DoesNotContain(report.Session.SessionId, json);
        foreach (var t in report.Session.Turns.Where(t => t.Prompt.Length > 8))
            Assert.DoesNotContain(t.Prompt[..8], json);
        foreach (var target in report.Session.Turns.SelectMany(t => t.Tools).Select(x => x.Target).OfType<string>())
            Assert.DoesNotContain(target, json);

        var hashed = TeamExport.Build("alex", [report], hashProjects: true, DateTimeOffset.UnixEpoch).ToJson();
        Assert.DoesNotContain("claims-api", hashed);
    }

    [Fact]
    public void Rollup_merges_repeat_exports_and_totals_by_person_and_project()
    {
        var report = Fixture();
        var a1 = TeamExport.Parse(TeamExport.Build("alex", [report], false, DateTimeOffset.UnixEpoch).ToJson());
        var a2 = TeamExport.Parse(TeamExport.Build("Alex", [report], false, DateTimeOffset.UnixEpoch.AddDays(1)).ToJson());
        var b = TeamExport.Parse(TeamExport.Build("sam", [report], false, DateTimeOffset.UnixEpoch).ToJson());

        var merged = TeamRollup.Merge([a1, a2, b]);
        Assert.Equal(2, merged.Count);
        Assert.All(merged, e => Assert.Single(e.Sessions)); // same session exported twice counts once

        var total = TeamRollup.Total(merged);
        Assert.Equal(2, total.People);
        Assert.Equal(2, total.Sessions);
        Assert.Equal(2 * report.TotalCost!.Value, total.Cost);
        Assert.True(total.WastedShare > 0);
        Assert.Equal("claims-api", Assert.Single(TeamRollup.ByProject(merged)).Key);
        Assert.Equal(2, TeamRollup.ByPerson(merged).Count);
    }

    [Fact]
    public void Newer_schema_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => TeamExport.Parse("""{"schema": 99, "person": "x", "sessions": []}"""));
    }
}

public class EvalTests
{
    [Fact]
    public void Scores_precision_recall_and_confusion()
    {
        var r = Evaluator.Score([
            (TurnOutcome.Wasted, TurnOutcome.Wasted),
            (TurnOutcome.Wasted, TurnOutcome.Productive),
            (TurnOutcome.Productive, TurnOutcome.Productive),
            (TurnOutcome.Exploratory, TurnOutcome.Wasted),
        ]);

        Assert.Equal(0.5, r.Accuracy);
        var wasted = Assert.Single(r.Classes, c => c.Outcome == TurnOutcome.Wasted);
        Assert.Equal(0.5, wasted.Precision);
        Assert.Equal(0.5, wasted.Recall);
        Assert.Equal(1, r.Confusion[(TurnOutcome.Wasted, TurnOutcome.Productive)]);
        Assert.DoesNotContain(r.Classes, c => c.Outcome == TurnOutcome.Failing);
    }

    [Fact]
    public void Label_store_keeps_the_latest_label_per_turn()
    {
        var path = Path.Combine(Path.GetTempPath(), "turnroi-labels-" + Guid.NewGuid().ToString("N") + ".jsonl");
        LabelStore.Append(new TurnLabel { Source = "claude", Session = "s", Turn = 1, Label = "Wasted", Predicted = "Productive" }, path);
        LabelStore.Append(new TurnLabel { Source = "claude", Session = "s", Turn = 1, Label = "Productive", Predicted = "Productive" }, path);
        LabelStore.Append(new TurnLabel { Source = "claude", Session = "s", Turn = 2, Label = "bogus" }, path);

        var label = Assert.Single(LabelStore.Read(path));
        Assert.Equal("Productive", label.Label);
    }

    [Theory]
    [InlineData("w", TurnOutcome.Wasted)]
    [InlineData("Exploratory", TurnOutcome.Exploratory)]
    [InlineData(" P ", TurnOutcome.Productive)]
    public void Parses_label_shorthands(string text, TurnOutcome expected)
    {
        Assert.Equal(expected, LabelStore.Outcome(text));
        Assert.Null(LabelStore.Outcome("unknown"));
    }
}
