using TurnRoi.Core.Bench;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Core.Tests;

public class BenchTests
{
    private static BenchRun Run(string task, string variant, bool ok, decimal cost, int attempt = 1) =>
        new(task, variant, attempt, ok, false, 0, TimeSpan.Zero, "s", 1000, cost, 1, 0, null);

    [Fact]
    public void Summary_compares_cost_only_on_tasks_every_variant_solved()
    {
        var runs = new[]
        {
            Run("a", "baseline", true, 1.00m), Run("a", "turnroi", true, 0.60m),
            Run("b", "baseline", true, 2.00m), Run("b", "turnroi", false, 0.10m),
            Run("c", "baseline", false, 3.00m), Run("c", "turnroi", true, 0.50m),
        };

        var s = BenchRunner.Summarize(runs);

        var baseline = Assert.Single(s, v => v.Variant == "baseline");
        var turnroi = Assert.Single(s, v => v.Variant == "turnroi");
        Assert.Equal(1, baseline.SharedSuccessTasks);
        Assert.Equal(1.00m, baseline.CostOnSharedSuccesses);
        Assert.Equal(0.60m, turnroi.CostOnSharedSuccesses);
        Assert.Equal(2, turnroi.Successes);
        Assert.Equal(6.00m / 2, baseline.CostPerSuccess);
    }

    [Fact]
    public void Shell_quote_survives_quotes()
    {
        if (OperatingSystem.IsWindows()) return;
        var r = Shell.Run("printf %s " + Shell.Quote("it's \"fine\" $HOME"), Path.GetTempPath(), null, TimeSpan.FromSeconds(30));
        Assert.Equal("it's \"fine\" $HOME", r.Output.Trim());
    }

    [Fact]
    public void Runs_a_fake_agent_per_variant_and_measures_its_session()
    {
        if (OperatingSystem.IsWindows()) return; // the fake agent is a POSIX shell script

        var root = Path.Combine(Path.GetTempPath(), "turnroi-bench-test-" + Guid.NewGuid().ToString("N")[..8]);
        var repo = Path.Combine(root, "repo");
        var sessions = Path.Combine(root, "sessions", "proj");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(repo, "README.md"), "demo");
        Directory.CreateDirectory(Path.Combine(repo, "bin"));
        File.WriteAllText(Path.Combine(repo, "bin", "big.dll"), "x");

        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-code-basic.jsonl");
        // Writes a Claude Code transcript whose cwd is the run's copy; the lean variant drops the Haiku calls.
        var agent = Path.Combine(root, "agent.sh");
        File.WriteAllText(agent,
            "#!/bin/sh\n" +
            "test -d bin && exit 3\n" +
            $"out='{sessions}'/$TURNROI_TASK-$TURNROI_VARIANT-$$.jsonl\n" +
            $"if [ -f .lean ]; then grep -v haiku '{fixture}'; else cat '{fixture}'; fi | sed \"s#/repo/claims-api#$PWD#g\" > \"$out\"\n" +
            "[ \"$1\" = 'fail me' ] || touch done.txt\n");
        File.WriteAllText(Path.Combine(root, "suite.json"), $$"""
            {
              "repo": "repo",
              "agent": "sh '{{agent}}' {prompt}",
              "source": "claude",
              "sessionsRoot": "sessions",
              "verify": "test -f done.txt",
              "variants": { "baseline": [], "turnroi": ["touch .lean"] },
              "tasks": [ { "id": "t1", "prompt": "do it" }, { "id": "t2", "prompt": "fail me" } ]
            }
            """);

        var suite = BenchSuite.Load(Path.Combine(root, "suite.json"));
        var runs = new BenchRunner(suite, PriceTable.LoadDefault()).Run();

        Assert.Equal(4, runs.Count);
        Assert.All(runs, r => Assert.NotNull(r.SessionId));
        Assert.True(runs.Single(r => r.Task == "t1" && r.Variant == "baseline").Success);
        Assert.False(runs.Single(r => r.Task == "t2" && r.Variant == "turnroi").Success);

        var summary = BenchRunner.Summarize(runs);
        var baseline = summary.Single(v => v.Variant == "baseline").CostOnSharedSuccesses!.Value;
        var lean = summary.Single(v => v.Variant == "turnroi").CostOnSharedSuccesses!.Value;
        Assert.True(lean < baseline, $"lean {lean} should cost less than baseline {baseline}");
        Assert.Contains("\"variant\": \"turnroi\"", BenchRunner.ToJson(runs));
    }
}
