using TurnRoi.Core.Bench;
using TurnRoi.Core.Pricing;

namespace TurnRoi.Cli;

/// <summary><c>turnroi bench &lt;suite.json&gt;</c>: same tasks, several variants, cost at equal success.</summary>
internal static class BenchCommand
{
    public static int Run(CliArgs opts, PriceTable prices)
    {
        if (opts.Target is null)
        {
            Console.Error.WriteLine("error: bench needs a suite file: turnroi bench suite.json (see docs/BENCHMARK.md)");
            return 2;
        }

        BenchSuite suite;
        try { suite = BenchSuite.Load(opts.Target); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: could not load {opts.Target}: {ex.Message}");
            return 1;
        }
        if (opts.LimitSet) suite.Runs = opts.Limit;

        var only = opts.Variants.Count == 0 ? null : opts.Variants;
        var runs = new BenchRunner(suite, prices, msg => Console.Error.WriteLine(msg)).Run(only);
        if (runs.Count == 0)
        {
            Console.Error.WriteLine("error: no runs (check --variant names against the suite's \"variants\")");
            return 1;
        }

        if (opts.WritePath is not null)
        {
            File.WriteAllText(opts.WritePath, BenchRunner.ToJson(runs));
            Console.Error.WriteLine($"Wrote {opts.WritePath}");
        }
        if (opts.Json)
        {
            Console.WriteLine(BenchRunner.ToJson(runs));
            return 0;
        }

        var w = Console.Out;
        var summary = BenchRunner.Summarize(runs);
        w.WriteLine();
        w.WriteLine(Ansi.Bold("Benchmark"));
        var t = new TextTable("Variant", "Success", "Cost", "Tokens", "Cost / success", "Cost on shared successes");
        foreach (var s in summary)
            t.Add(s.Variant, $"{s.Successes}/{s.Runs}", Money(s.TotalCost), s.TotalTokens.ToString("N0"), Money(s.CostPerSuccess),
                s.CostOnSharedSuccesses is null ? "-" : $"{Money(s.CostOnSharedSuccesses)} ({s.SharedSuccessTasks} task(s))");
        t.Write(w, "  ");

        if (summary.Count >= 2 && summary[0].CostOnSharedSuccesses is { } a && a > 0)
        {
            w.WriteLine();
            foreach (var s in summary.Skip(1))
                if (s.CostOnSharedSuccesses is { } b)
                {
                    var delta = (b - a) / a;
                    var line = $"  {s.Variant} vs {summary[0].Variant}: {delta:+0%;-0%;0%} cost on the tasks both solved, " +
                               $"success {s.Successes}/{s.Runs} vs {summary[0].Successes}/{summary[0].Runs}.";
                    w.WriteLine(delta < 0 && s.Successes >= summary[0].Successes ? Ansi.Green(line) : line);
                }
        }

        var failed = runs.Where(r => r.Note is not null).ToList();
        if (failed.Count > 0)
        {
            w.WriteLine();
            foreach (var r in failed) w.WriteLine(Ansi.Yellow($"  {r.Task} · {r.Variant} · run {r.Attempt}: {r.Note}"));
        }
        w.WriteLine();
        w.WriteLine(Ansi.Dim("  Agents are noisy: use several tasks and --runs 3+ before trusting a difference."));
        return 0;
    }

    private static string Money(decimal? d) => d is { } v ? $"${v:N2}" : "-";
}
